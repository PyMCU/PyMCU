/*
 * -----------------------------------------------------------------------------
 * PyMCU — pymcu-xtensa extension
 * Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
 *
 * SPDX-License-Identifier: MIT
 * -----------------------------------------------------------------------------
 */

// Xtensa LLVM IR codegen backend for PyMCU.
//
// Emits LLVM IR (.ll) that can be compiled by xtensa-esp-elf-clang from
// ESP-IDF 5.x+ (which bundles a custom LLVM fork with Xtensa backend support).
//
// Target triples used:
//   ESP8266 / LX106 — xtensa-esp8266-none-elf
//   ESP32   / LX6   — xtensa-esp32-elf
//   ESP32-S2 / LX7  — xtensa-esp32s2-elf
//   ESP32-S3 / LX7  — xtensa-esp32s3-elf
//
// Compilation strategy:
//   All local variables are alloca'd in the entry block; LLVM's mem2reg pass
//   promotes them to SSA registers automatically.  This avoids the need to
//   track SSA value numbers here, at the cost of slightly more verbose IR.
//
// Usage (compile the .ll output):
//   xtensa-esp-elf-clang --target=xtensa-esp32-elf -O2 firmware.ll -o firmware.elf \
//     -nostdlib -nostartfiles -T linker.ld

using PyMCU.Backend.Analysis;
using PyMCU.Common.Models;
using PyMCU.IR;

namespace PyMCU.Backend.Targets.Xtensa;

public class XtensaLlvmCodeGen(DeviceConfig cfg) : CodeGen
{
    private TextWriter _out = TextWriter.Null;
    private int _tmpCounter;
    private int _labelCounter;
    private bool _needsTerminator;
    private Dictionary<string, string> _allocas = [];   // var/tmp name → alloca'd name (%v_<name>)
    private HashSet<string> _declaredFuncs = [];
    // Module-level globals (scalar + array) — bypass alloca, access via @name.
    private HashSet<string> _globals = [];
    // Flash/rodata arrays: name → bytes.
    private Dictionary<string, List<int>> _flashArrays = [];

    // -------------------------------------------------------------------------
    // Target triple / data layout helpers
    // -------------------------------------------------------------------------

    private string TargetTriple()
    {
        var chip = cfg.Chip.ToLowerInvariant();
        if (chip.StartsWith("esp8266") || chip == "lx106")
            return "xtensa-esp8266-none-elf";
        if (chip.StartsWith("esp32s3") || chip.StartsWith("esp32-s3"))
            return "xtensa-esp32s3-elf";
        if (chip.StartsWith("esp32s2") || chip.StartsWith("esp32-s2"))
            return "xtensa-esp32s2-elf";
        return "xtensa-esp32-elf";
    }

    // Standard 32-bit little-endian data layout for Xtensa.
    private static string DataLayout => "e-m:e-p:32:32-i64:64-n32";

    // -------------------------------------------------------------------------
    // IR value helpers
    // -------------------------------------------------------------------------

    // LLVM type string for a DataType.
    private static string LlvmType(DataType dt) => dt switch
    {
        DataType.UINT8  => "i8",
        DataType.INT8   => "i8",
        DataType.UINT16 => "i16",
        DataType.INT16  => "i16",
        DataType.FLOAT  => "float",
        _               => "i32"
    };

    // LLVM type for a Val (all integer types mapped to i32 for simplicity).
    private static string LlvmType(Val _) => "i32";

    // Return a fresh %tN SSA name.
    private string FreshTmp() => $"%t{_tmpCounter++}";

    // Return the alloca pointer name for a variable/temporary.
    private string AllocaName(string name) => $"%v_{name.Replace('.', '_')}";

    // Coerce a value to i32 if its natural type is narrower.
    private string CoerceToI32(string val, DataType dt)
    {
        if (dt is DataType.UINT8 or DataType.UINT16)
        {
            string ext = FreshTmp();
            _out.WriteLine($"  {ext} = zext {LlvmType(dt)} {val} to i32");
            return ext;
        }
        if (dt is DataType.INT8 or DataType.INT16)
        {
            string ext = FreshTmp();
            _out.WriteLine($"  {ext} = sext {LlvmType(dt)} {val} to i32");
            return ext;
        }
        return val;
    }

    // Emit a load of a Val into a fresh %tN and return that name (always i32).
    private string EmitLoad(Val v)
    {
        switch (v)
        {
            case Constant c:
                return c.Value.ToString();
            case FloatConstant fc:
                return ((int)BitConverter.SingleToUInt32Bits((float)fc.Value)).ToString();
            case MemoryAddress ma:
            {
                string ptrTmp = FreshTmp();
                string valTmp = FreshTmp();
                _out.WriteLine($"  {ptrTmp} = inttoptr i32 {ma.Address} to i32*");
                _out.WriteLine($"  {valTmp} = load i32, i32* {ptrTmp}, align 4");
                return valTmp;
            }
            case Variable vr:
            {
                string tmp = FreshTmp();
                if (_globals.Contains(vr.Name))
                {
                    string llT = LlvmType(vr.Type);
                    string safeName = vr.Name.Replace('.', '_');
                    _out.WriteLine($"  {tmp} = load {llT}, {llT}* @{safeName}, align 4");
                    return CoerceToI32(tmp, vr.Type);
                }
                _out.WriteLine($"  {tmp} = load i32, i32* {AllocaName(vr.Name)}, align 4");
                return tmp;
            }
            case Temporary tr:
            {
                string tmp = FreshTmp();
                _out.WriteLine($"  {tmp} = load i32, i32* {AllocaName(tr.Name)}, align 4");
                return tmp;
            }
            default:
                return "0";
        }
    }

    // Emit a store of an i32 value (named SSA value or literal) into a Val destination.
    private void EmitStore(string valStr, Val dst)
    {
        switch (dst)
        {
            case MemoryAddress ma:
            {
                string ptrTmp = FreshTmp();
                _out.WriteLine($"  {ptrTmp} = inttoptr i32 {ma.Address} to i32*");
                _out.WriteLine($"  store i32 {valStr}, i32* {ptrTmp}, align 4");
                break;
            }
            case Variable vr:
                if (_globals.Contains(vr.Name))
                {
                    string llT = LlvmType(vr.Type);
                    string safeName = vr.Name.Replace('.', '_');
                    // Truncate back to the natural width if needed.
                    string toStore = valStr;
                    if (llT != "i32")
                    {
                        string trunc = FreshTmp();
                        _out.WriteLine($"  {trunc} = trunc i32 {valStr} to {llT}");
                        toStore = trunc;
                    }
                    _out.WriteLine($"  store {llT} {toStore}, {llT}* @{safeName}, align 4");
                }
                else
                    _out.WriteLine($"  store i32 {valStr}, i32* {AllocaName(vr.Name)}, align 4");
                break;
            case Temporary tr:
                _out.WriteLine($"  store i32 {valStr}, i32* {AllocaName(tr.Name)}, align 4");
                break;
        }
    }

    // Declare a function name once in the module (as external declaration).
    private void EnsureDeclared(string funcName)
    {
        if (_declaredFuncs.Add(funcName))
            _out.WriteLine($"declare i32 @{funcName}(...)");
    }

    // -------------------------------------------------------------------------
    // Top-level compilation entry point
    // -------------------------------------------------------------------------

    public override void Compile(ProgramIR program, TextWriter output)
    {
        _out = output;
        _globals.Clear();
        _flashArrays.Clear();

        // Register global scalar names.
        foreach (var g in program.Globals)
            _globals.Add(g.Name);

        // Pre-scan function bodies for array and flash data names.
        var arrayDefs = new Dictionary<string, (DataType ElemType, int Count)>();
        foreach (var fn in program.Functions)
        {
            foreach (var instr in fn.Body)
            {
                switch (instr)
                {
                    case ArrayLoad al when !_globals.Contains(al.ArrayName)
                                       && !arrayDefs.ContainsKey(al.ArrayName):
                        arrayDefs[al.ArrayName] = (al.ElemType, al.Count);
                        _globals.Add(al.ArrayName);
                        break;
                    case ArrayStore ast when !_globals.Contains(ast.ArrayName)
                                         && !arrayDefs.ContainsKey(ast.ArrayName):
                        arrayDefs[ast.ArrayName] = (ast.ElemType, ast.Count);
                        _globals.Add(ast.ArrayName);
                        break;
                    case FlashData fd:
                        _flashArrays[fd.Name] = fd.Bytes;
                        break;
                }
            }
        }

        _out.WriteLine($"; Generated by pymcuc for Xtensa LLVM IR ({cfg.Chip})");
        _out.WriteLine($"target datalayout = \"{DataLayout}\"");
        _out.WriteLine($"target triple = \"{TargetTriple()}\"");
        _out.WriteLine();

        // Emit extern symbol declarations.
        foreach (var sym in program.ExternSymbols)
            _out.WriteLine($"declare void @{sym}(...)");
        if (program.ExternSymbols.Count > 0)
            _out.WriteLine();

        // Emit global scalar variables.
        foreach (var g in program.Globals)
        {
            if (_flashArrays.ContainsKey(g.Name)) continue;
            var llT      = LlvmType(g.Type);
            var safeName = g.Name.Replace('.', '_');
            _out.WriteLine($"@{safeName} = global {llT} 0");
        }

        // Emit global SRAM array variables.
        foreach (var (name, (elemType, count)) in arrayDefs)
        {
            var llT      = LlvmType(elemType);
            var safeName = name.Replace('.', '_');
            _out.WriteLine($"@{safeName} = global [{count} x {llT}] zeroinitializer");
        }

        // Emit read-only flash/rodata arrays with their actual byte data.
        foreach (var (name, bytes) in _flashArrays)
        {
            var safeName  = name.Replace('.', '_');
            var byteInits = string.Join(", ", bytes.Select(b => $"i8 {b}"));
            _out.WriteLine($"@{safeName} = constant [{bytes.Count} x i8] [{byteInits}]");
        }

        bool hasGlobals = program.Globals.Count > 0 || arrayDefs.Count > 0 || _flashArrays.Count > 0;
        if (hasGlobals) _out.WriteLine();

        // Forward-declare all program functions so calls in any order work.
        foreach (var func in program.Functions)
            _declaredFuncs.Add(func.Name);

        foreach (var func in program.Functions)
            CompileFunction(func);
    }

    public override void EmitContextSave() { }
    public override void EmitContextRestore() { }
    public override void EmitInterruptReturn() { }

    // -------------------------------------------------------------------------
    // Function compilation
    // -------------------------------------------------------------------------

    private void CompileFunction(Function func)
    {
        _tmpCounter = 0;
        _labelCounter = 0;
        _needsTerminator = true;
        _allocas = [];

        // Collect all variable/temporary names referenced in the function body.
        CollectAllocas(func);

        // Determine LLVM parameter list from Function.Params.
        var paramList = string.Join(", ", func.Params.Select((p, i) => $"i32 %arg{i}"));
        string retType = func.ReturnType == DataType.VOID ? "void" : "i32";
        _out.WriteLine($"define {retType} @{func.Name}({paramList}) {{");
        _out.WriteLine("entry:");
        _needsTerminator = false;

        // Emit alloca for each variable / temp.
        foreach (var (name, allocaName) in _allocas)
            _out.WriteLine($"  {allocaName} = alloca i32, align 4");

        // Store incoming parameters into their alloca'd slots.
        for (int i = 0; i < func.Params.Count; i++)
        {
            string pName = func.Params[i];
            if (_allocas.ContainsKey(pName))
                _out.WriteLine($"  store i32 %arg{i}, i32* {AllocaName(pName)}, align 4");
        }

        foreach (var instr in func.Body)
            CompileInstruction(func, instr);

        // Close any unterminated block.
        if (_needsTerminator)
        {
            if (retType == "void") _out.WriteLine("  ret void");
            else                   _out.WriteLine("  ret i32 0");
        }

        _out.WriteLine("}");
        _out.WriteLine();
    }

    // Walk all instructions and collect every Variable/Temporary name.
    private void CollectAllocas(Function func)
    {
        foreach (var p in func.Params)
        {
            string alloca = AllocaName(p);
            _allocas[p] = alloca;
        }

        void AddVal(Val v)
        {
            switch (v)
            {
                case Variable vr:
                    if (!_globals.Contains(vr.Name))
                        _allocas.TryAdd(vr.Name, AllocaName(vr.Name));
                    break;
                case Temporary tr:
                    _allocas.TryAdd(tr.Name, AllocaName(tr.Name));
                    break;
            }
        }

        foreach (var instr in func.Body)
        {
            switch (instr)
            {
                case Copy i:           AddVal(i.Src);  AddVal(i.Dst); break;
                case Return i:         AddVal(i.Value); break;
                case Unary i:          AddVal(i.Src);  AddVal(i.Dst); break;
                case Binary i:         AddVal(i.Src1); AddVal(i.Src2); AddVal(i.Dst); break;
                case Call i:
                    foreach (var a in i.Args) AddVal(a);
                    AddVal(i.Dst);
                    break;
                case JumpIfZero i:         AddVal(i.Condition); break;
                case JumpIfNotZero i:      AddVal(i.Condition); break;
                case JumpIfEqual i:        AddVal(i.Src1); AddVal(i.Src2); break;
                case JumpIfNotEqual i:     AddVal(i.Src1); AddVal(i.Src2); break;
                case JumpIfLessThan i:     AddVal(i.Src1); AddVal(i.Src2); break;
                case JumpIfLessOrEqual i:  AddVal(i.Src1); AddVal(i.Src2); break;
                case JumpIfGreaterThan i:  AddVal(i.Src1); AddVal(i.Src2); break;
                case JumpIfGreaterOrEqual i: AddVal(i.Src1); AddVal(i.Src2); break;
                case JumpIfBitSet i:   AddVal(i.Source); break;
                case JumpIfBitClear i: AddVal(i.Source); break;
                case BitSet i:         AddVal(i.Target); break;
                case BitClear i:       AddVal(i.Target); break;
                case BitCheck i:       AddVal(i.Source); AddVal(i.Dst); break;
                case BitWrite i:       AddVal(i.Target); AddVal(i.Src); break;
                case AugAssign i:      AddVal(i.Target); AddVal(i.Operand); break;
                case LoadIndirect i:   AddVal(i.SrcPtr); AddVal(i.Dst); break;
                case StoreIndirect i:  AddVal(i.Src); AddVal(i.DstPtr); break;
                case ArrayLoad i:      AddVal(i.Index); AddVal(i.Dst); break;
                case ArrayStore i:     AddVal(i.Index); AddVal(i.Src); break;
                case ArrayLoadFlash i: AddVal(i.Index); AddVal(i.Dst); break;
            }
        }
    }

    // -------------------------------------------------------------------------
    // Instruction dispatch
    // -------------------------------------------------------------------------

    private void CompileInstruction(Function func, Instruction instr)
    {
        switch (instr)
        {
            case Copy arg:           CompileCopy(arg); break;
            case Return arg:         CompileReturn(func, arg); break;
            case Jump arg:           CompileJump(arg); break;
            case JumpIfZero arg:     CompileJumpIfZero(arg); break;
            case JumpIfNotZero arg:  CompileJumpIfNotZero(arg); break;
            case JumpIfEqual arg:    CompileJumpIfEqual(arg); break;
            case JumpIfNotEqual arg: CompileJumpIfNotEqual(arg); break;
            case JumpIfLessThan arg:     CompileJumpIfRelational(arg.Src1, arg.Src2, "slt", arg.Target); break;
            case JumpIfLessOrEqual arg:  CompileJumpIfRelational(arg.Src1, arg.Src2, "sle", arg.Target); break;
            case JumpIfGreaterThan arg:  CompileJumpIfRelational(arg.Src1, arg.Src2, "sgt", arg.Target); break;
            case JumpIfGreaterOrEqual arg: CompileJumpIfRelational(arg.Src1, arg.Src2, "sge", arg.Target); break;
            case JumpIfBitSet arg:   CompileJumpIfBit(arg.Source, arg.Bit, true, arg.Target); break;
            case JumpIfBitClear arg: CompileJumpIfBit(arg.Source, arg.Bit, false, arg.Target); break;
            case Label arg:          CompileLabel(arg.Name); break;
            case Call arg:           CompileCall(arg); break;
            case Unary arg:          CompileUnary(arg); break;
            case Binary arg:         CompileBinary(arg); break;
            case BitSet arg:         CompileBitSet(arg); break;
            case BitClear arg:       CompileBitClear(arg); break;
            case BitCheck arg:       CompileBitCheck(arg); break;
            case BitWrite arg:       CompileBitWrite(arg); break;
            case AugAssign arg:      CompileAugAssign(arg); break;
            case InlineAsm arg:
                // Inline asm is emitted as a module-level asm block comment;
                // the LLVM IR inline asm syntax requires operand constraints
                // that cannot be inferred here.
                _out.WriteLine($"  ; inline asm: {arg.Code}");
                break;
            case DebugLine arg:
                _out.WriteLine($"  ; {arg.SourceFile}:{arg.Line}: {arg.Text}");
                break;
            case ArrayLoad arg:      CompileLlvmArrayLoad(arg); break;
            case ArrayStore arg:     CompileLlvmArrayStore(arg); break;
            case ArrayLoadFlash arg: CompileLlvmArrayLoadFlash(arg); break;
            case FlashData:          break; // collected during Compile() pre-scan
            case LoadIndirect arg:   CompileLlvmLoadIndirect(arg); break;
            case StoreIndirect arg:  CompileLlvmStoreIndirect(arg); break;
        }
    }

    // -------------------------------------------------------------------------
    // Label / block transitions
    // -------------------------------------------------------------------------

    private void CompileLabel(string name)
    {
        // Close the previous block if it lacks a terminator.
        if (_needsTerminator)
            _out.WriteLine($"  br label %{LlvmLabel(name)}");
        _out.WriteLine($"{LlvmLabel(name)}:");
        _needsTerminator = true;
    }

    private static string LlvmLabel(string name) =>
        name.StartsWith('.') ? name[1..] : name;

    // -------------------------------------------------------------------------
    // Return
    // -------------------------------------------------------------------------

    private void CompileReturn(Function func, Return arg)
    {
        if (func.Name == "main")
        {
            // Bare-metal: spin forever (same as GAS backend).
            string loopLabel = $"end_loop_{_labelCounter++}";
            _out.WriteLine($"  br label %{loopLabel}");
            _out.WriteLine($"{loopLabel}:");
            _out.WriteLine($"  br label %{loopLabel}");
        }
        else if (arg.Value is NoneVal || func.ReturnType == DataType.VOID)
        {
            _out.WriteLine("  ret void");
        }
        else
        {
            string v = EmitLoad(arg.Value);
            _out.WriteLine($"  ret i32 {v}");
        }
        _needsTerminator = false;
    }

    // -------------------------------------------------------------------------
    // Copy
    // -------------------------------------------------------------------------

    private void CompileCopy(Copy arg)
    {
        string v = EmitLoad(arg.Src);
        EmitStore(v, arg.Dst);
    }

    // -------------------------------------------------------------------------
    // Jump
    // -------------------------------------------------------------------------

    private void CompileJump(Jump arg)
    {
        _out.WriteLine($"  br label %{LlvmLabel(arg.Target)}");
        _needsTerminator = false;
    }

    private void CompileJumpIfZero(JumpIfZero arg)
    {
        string v    = EmitLoad(arg.Condition);
        string cond = FreshTmp();
        string fall = $"fall_{_labelCounter++}";
        _out.WriteLine($"  {cond} = icmp eq i32 {v}, 0");
        _out.WriteLine($"  br i1 {cond}, label %{LlvmLabel(arg.Target)}, label %{fall}");
        _out.WriteLine($"{fall}:");
        _needsTerminator = true;
    }

    private void CompileJumpIfNotZero(JumpIfNotZero arg)
    {
        string v    = EmitLoad(arg.Condition);
        string cond = FreshTmp();
        string fall = $"fall_{_labelCounter++}";
        _out.WriteLine($"  {cond} = icmp ne i32 {v}, 0");
        _out.WriteLine($"  br i1 {cond}, label %{LlvmLabel(arg.Target)}, label %{fall}");
        _out.WriteLine($"{fall}:");
        _needsTerminator = true;
    }

    private void CompileJumpIfEqual(JumpIfEqual arg)
    {
        string a    = EmitLoad(arg.Src1);
        string b    = EmitLoad(arg.Src2);
        string cond = FreshTmp();
        string fall = $"fall_{_labelCounter++}";
        _out.WriteLine($"  {cond} = icmp eq i32 {a}, {b}");
        _out.WriteLine($"  br i1 {cond}, label %{LlvmLabel(arg.Target)}, label %{fall}");
        _out.WriteLine($"{fall}:");
        _needsTerminator = true;
    }

    private void CompileJumpIfNotEqual(JumpIfNotEqual arg)
    {
        string a    = EmitLoad(arg.Src1);
        string b    = EmitLoad(arg.Src2);
        string cond = FreshTmp();
        string fall = $"fall_{_labelCounter++}";
        _out.WriteLine($"  {cond} = icmp ne i32 {a}, {b}");
        _out.WriteLine($"  br i1 {cond}, label %{LlvmLabel(arg.Target)}, label %{fall}");
        _out.WriteLine($"{fall}:");
        _needsTerminator = true;
    }

    private void CompileJumpIfRelational(Val src1, Val src2, string pred, string target)
    {
        // Choose signed vs. unsigned predicate based on operand types.
        if (pred is "slt" or "sle" or "sgt" or "sge")
        {
            bool isUnsigned = IsUnsignedLlvmVal(src1) || IsUnsignedLlvmVal(src2);
            if (isUnsigned)
                pred = pred.Replace('s', 'u');
        }
        string a    = EmitLoad(src1);
        string b    = EmitLoad(src2);
        string cond = FreshTmp();
        string fall = $"fall_{_labelCounter++}";
        _out.WriteLine($"  {cond} = icmp {pred} i32 {a}, {b}");
        _out.WriteLine($"  br i1 {cond}, label %{LlvmLabel(target)}, label %{fall}");
        _out.WriteLine($"{fall}:");
        _needsTerminator = true;
    }

    private void CompileJumpIfBit(Val source, int bit, bool setNotClear, string target)
    {
        string v    = EmitLoad(source);
        string mask = FreshTmp();
        string cond = FreshTmp();
        string fall = $"fall_{_labelCounter++}";
        _out.WriteLine($"  {mask} = and i32 {v}, {1 << bit}");
        string pred = setNotClear ? "ne" : "eq";
        _out.WriteLine($"  {cond} = icmp {pred} i32 {mask}, 0");
        _out.WriteLine($"  br i1 {cond}, label %{LlvmLabel(target)}, label %{fall}");
        _out.WriteLine($"{fall}:");
        _needsTerminator = true;
    }

    // -------------------------------------------------------------------------
    // Call
    // -------------------------------------------------------------------------

    private void CompileCall(Call arg)
    {
        EnsureDeclared(arg.FunctionName);
        var args = arg.Args.Select(a => $"i32 {EmitLoad(a)}");
        string argStr = string.Join(", ", args);
        if (arg.Dst is NoneVal)
        {
            _out.WriteLine($"  call i32 @{arg.FunctionName}({argStr})");
        }
        else
        {
            string ret = FreshTmp();
            _out.WriteLine($"  {ret} = call i32 @{arg.FunctionName}({argStr})");
            EmitStore(ret, arg.Dst);
        }
    }

    // -------------------------------------------------------------------------
    // Unary
    // -------------------------------------------------------------------------

    private void CompileUnary(Unary arg)
    {
        string v   = EmitLoad(arg.Src);
        string res = FreshTmp();
        switch (arg.Op)
        {
            case UnaryOp.Neg:
                _out.WriteLine($"  {res} = sub i32 0, {v}");
                break;
            case UnaryOp.BitNot:
                _out.WriteLine($"  {res} = xor i32 {v}, -1");
                break;
            case UnaryOp.Not:
            {
                string cmp = FreshTmp();
                _out.WriteLine($"  {cmp} = icmp eq i32 {v}, 0");
                _out.WriteLine($"  {res} = zext i1 {cmp} to i32");
                break;
            }
        }
        EmitStore(res, arg.Dst);
    }

    // -------------------------------------------------------------------------
    // Binary
    // -------------------------------------------------------------------------

    private void CompileBinary(Binary arg)
    {
        string a = EmitLoad(arg.Src1);
        string b = EmitLoad(arg.Src2);
        string res = FreshTmp();
        bool isUnsigned = IsUnsignedLlvmVal(arg.Src1) || IsUnsignedLlvmVal(arg.Src2);

        switch (arg.Op)
        {
            case BinaryOp.Add:      _out.WriteLine($"  {res} = add nsw i32 {a}, {b}"); break;
            case BinaryOp.Sub:      _out.WriteLine($"  {res} = sub nsw i32 {a}, {b}"); break;
            case BinaryOp.Mul:      _out.WriteLine($"  {res} = mul nsw i32 {a}, {b}"); break;
            case BinaryOp.Div:
            case BinaryOp.FloorDiv:
                _out.WriteLine(isUnsigned
                    ? $"  {res} = udiv i32 {a}, {b}"
                    : $"  {res} = sdiv i32 {a}, {b}");
                break;
            case BinaryOp.Mod:
                _out.WriteLine(isUnsigned
                    ? $"  {res} = urem i32 {a}, {b}"
                    : $"  {res} = srem i32 {a}, {b}");
                break;
            case BinaryOp.BitAnd:   _out.WriteLine($"  {res} = and i32 {a}, {b}"); break;
            case BinaryOp.BitOr:    _out.WriteLine($"  {res} = or i32 {a}, {b}"); break;
            case BinaryOp.BitXor:   _out.WriteLine($"  {res} = xor i32 {a}, {b}"); break;
            case BinaryOp.LShift:   _out.WriteLine($"  {res} = shl i32 {a}, {b}"); break;
            case BinaryOp.RShift:
                _out.WriteLine(isUnsigned
                    ? $"  {res} = lshr i32 {a}, {b}"
                    : $"  {res} = ashr i32 {a}, {b}");
                break;
            case BinaryOp.Equal:
            {
                string cmp = FreshTmp();
                _out.WriteLine($"  {cmp} = icmp eq i32 {a}, {b}");
                _out.WriteLine($"  {res} = zext i1 {cmp} to i32");
                break;
            }
            case BinaryOp.NotEqual:
            {
                string cmp = FreshTmp();
                _out.WriteLine($"  {cmp} = icmp ne i32 {a}, {b}");
                _out.WriteLine($"  {res} = zext i1 {cmp} to i32");
                break;
            }
            case BinaryOp.LessThan:
            {
                string cmp = FreshTmp();
                _out.WriteLine(isUnsigned
                    ? $"  {cmp} = icmp ult i32 {a}, {b}"
                    : $"  {cmp} = icmp slt i32 {a}, {b}");
                _out.WriteLine($"  {res} = zext i1 {cmp} to i32");
                break;
            }
            case BinaryOp.LessEqual:
            {
                string cmp = FreshTmp();
                _out.WriteLine(isUnsigned
                    ? $"  {cmp} = icmp ule i32 {a}, {b}"
                    : $"  {cmp} = icmp sle i32 {a}, {b}");
                _out.WriteLine($"  {res} = zext i1 {cmp} to i32");
                break;
            }
            case BinaryOp.GreaterThan:
            {
                string cmp = FreshTmp();
                _out.WriteLine(isUnsigned
                    ? $"  {cmp} = icmp ugt i32 {a}, {b}"
                    : $"  {cmp} = icmp sgt i32 {a}, {b}");
                _out.WriteLine($"  {res} = zext i1 {cmp} to i32");
                break;
            }
            case BinaryOp.GreaterEqual:
            {
                string cmp = FreshTmp();
                _out.WriteLine(isUnsigned
                    ? $"  {cmp} = icmp uge i32 {a}, {b}"
                    : $"  {cmp} = icmp sge i32 {a}, {b}");
                _out.WriteLine($"  {res} = zext i1 {cmp} to i32");
                break;
            }
        }

        EmitStore(res, arg.Dst);
    }

    // -------------------------------------------------------------------------
    // Bit operations
    // -------------------------------------------------------------------------

    private void CompileBitSet(BitSet arg)
    {
        string v   = EmitLoad(arg.Target);
        string res = FreshTmp();
        _out.WriteLine($"  {res} = or i32 {v}, {1 << arg.Bit}");
        EmitStore(res, arg.Target);
    }

    private void CompileBitClear(BitClear arg)
    {
        string v   = EmitLoad(arg.Target);
        string res = FreshTmp();
        _out.WriteLine($"  {res} = and i32 {v}, {~(1 << arg.Bit)}");
        EmitStore(res, arg.Target);
    }

    private void CompileBitCheck(BitCheck arg)
    {
        string v    = EmitLoad(arg.Source);
        string shft = FreshTmp();
        string mask = FreshTmp();
        _out.WriteLine($"  {shft} = lshr i32 {v}, {arg.Bit}");
        _out.WriteLine($"  {mask} = and i32 {shft}, 1");
        EmitStore(mask, arg.Dst);
    }

    private void CompileBitWrite(BitWrite arg)
    {
        string src    = EmitLoad(arg.Src);
        string tgt    = EmitLoad(arg.Target);
        // Normalise src to 0 or 1.
        string cmp    = FreshTmp();
        string norm   = FreshTmp();
        _out.WriteLine($"  {cmp}  = icmp ne i32 {src}, 0");
        _out.WriteLine($"  {norm} = zext i1 {cmp} to i32");
        // Shift into position.
        string shifted = FreshTmp();
        _out.WriteLine($"  {shifted} = shl i32 {norm}, {arg.Bit}");
        // Clear bit in target, then OR with shifted src.
        string cleared = FreshTmp();
        string result  = FreshTmp();
        _out.WriteLine($"  {cleared} = and i32 {tgt}, {~(1 << arg.Bit)}");
        _out.WriteLine($"  {result}  = or i32 {cleared}, {shifted}");
        EmitStore(result, arg.Target);
    }

    // -------------------------------------------------------------------------
    // AugAssign
    // -------------------------------------------------------------------------

    private void CompileAugAssign(AugAssign arg)
    {
        // Lower to Binary + store back to target.
        string a   = EmitLoad(arg.Target);
        string b   = EmitLoad(arg.Operand);
        string res = FreshTmp();
        bool isUnsigned = IsUnsignedLlvmVal(arg.Target) || IsUnsignedLlvmVal(arg.Operand);
        string llvmOp = arg.Op switch
        {
            BinaryOp.Add      => "add nsw",
            BinaryOp.Sub      => "sub nsw",
            BinaryOp.Mul      => "mul nsw",
            BinaryOp.Div      => isUnsigned ? "udiv" : "sdiv",
            BinaryOp.FloorDiv => isUnsigned ? "udiv" : "sdiv",
            BinaryOp.Mod      => isUnsigned ? "urem" : "srem",
            BinaryOp.BitAnd   => "and",
            BinaryOp.BitOr    => "or",
            BinaryOp.BitXor   => "xor",
            BinaryOp.LShift   => "shl",
            BinaryOp.RShift   => isUnsigned ? "lshr" : "ashr",
            _                 => "add nsw"
        };
        _out.WriteLine($"  {res} = {llvmOp} i32 {a}, {b}");
        EmitStore(res, arg.Target);
    }

    // -------------------------------------------------------------------------
    // LoadIndirect / StoreIndirect  (pointer dereference)
    // -------------------------------------------------------------------------

    private void CompileLlvmLoadIndirect(LoadIndirect arg)
    {
        string ptrVal = EmitLoad(arg.SrcPtr);
        var dt     = arg.Dst is Variable vr ? vr.Type
                   : arg.Dst is Temporary tr ? tr.Type
                   : DataType.UINT32;
        string llT = LlvmType(dt);
        string ptr  = FreshTmp();
        string raw  = FreshTmp();
        _out.WriteLine($"  {ptr} = inttoptr i32 {ptrVal} to {llT}*");
        _out.WriteLine($"  {raw} = load {llT}, {llT}* {ptr}, align 1");
        string coerced = CoerceToI32(raw, dt);
        EmitStore(coerced, arg.Dst);
    }

    private void CompileLlvmStoreIndirect(StoreIndirect arg)
    {
        string srcVal  = EmitLoad(arg.Src);
        string ptrVal  = EmitLoad(arg.DstPtr);
        var dt     = arg.Src is Variable sv ? sv.Type
                   : arg.Src is Temporary st ? st.Type
                   : DataType.UINT32;
        string llT = LlvmType(dt);
        string ptr  = FreshTmp();
        _out.WriteLine($"  {ptr} = inttoptr i32 {ptrVal} to {llT}*");
        // Truncate to natural width if storing a narrower type.
        string toStore = srcVal;
        if (llT != "i32")
        {
            string trunc = FreshTmp();
            _out.WriteLine($"  {trunc} = trunc i32 {srcVal} to {llT}");
            toStore = trunc;
        }
        _out.WriteLine($"  store {llT} {toStore}, {llT}* {ptr}, align 1");
    }

    // -------------------------------------------------------------------------
    // ArrayLoad / ArrayStore  (SRAM variable-index arrays)
    // -------------------------------------------------------------------------

    private void CompileLlvmArrayLoad(ArrayLoad al)
    {
        string llT     = LlvmType(al.ElemType);
        string safeName = al.ArrayName.Replace('.', '_');
        string idxVal   = EmitLoad(al.Index);
        string gep      = FreshTmp();
        string raw      = FreshTmp();
        _out.WriteLine($"  {gep} = getelementptr inbounds [{al.Count} x {llT}], [{al.Count} x {llT}]* @{safeName}, i32 0, i32 {idxVal}");
        _out.WriteLine($"  {raw} = load {llT}, {llT}* {gep}, align 1");
        string coerced = CoerceToI32(raw, al.ElemType);
        EmitStore(coerced, al.Dst);
    }

    private void CompileLlvmArrayStore(ArrayStore ast)
    {
        string llT     = LlvmType(ast.ElemType);
        string safeName = ast.ArrayName.Replace('.', '_');
        string srcVal   = EmitLoad(ast.Src);
        string idxVal   = EmitLoad(ast.Index);
        string gep      = FreshTmp();
        _out.WriteLine($"  {gep} = getelementptr inbounds [{ast.Count} x {llT}], [{ast.Count} x {llT}]* @{safeName}, i32 0, i32 {idxVal}");
        string toStore = srcVal;
        if (llT != "i32")
        {
            string trunc = FreshTmp();
            _out.WriteLine($"  {trunc} = trunc i32 {srcVal} to {llT}");
            toStore = trunc;
        }
        _out.WriteLine($"  store {llT} {toStore}, {llT}* {gep}, align 1");
    }

    // -------------------------------------------------------------------------
    // ArrayLoadFlash  (read-only .rodata byte arrays)
    // -------------------------------------------------------------------------

    private void CompileLlvmArrayLoadFlash(ArrayLoadFlash alf)
    {
        string safeName = alf.ArrayName.Replace('.', '_');
        int count = _flashArrays.TryGetValue(alf.ArrayName, out var bytes) ? bytes.Count : 0;
        string idxVal = EmitLoad(alf.Index);
        string gep    = FreshTmp();
        string raw    = FreshTmp();
        string ext    = FreshTmp();
        _out.WriteLine($"  {gep} = getelementptr inbounds [{count} x i8], [{count} x i8]* @{safeName}, i32 0, i32 {idxVal}");
        _out.WriteLine($"  {raw} = load i8, i8* {gep}, align 1");
        _out.WriteLine($"  {ext} = zext i8 {raw} to i32");
        EmitStore(ext, alf.Dst);
    }

    // -------------------------------------------------------------------------
    // Helper: unsigned type detection
    // -------------------------------------------------------------------------

    private static bool IsUnsignedLlvmVal(Val v) => v switch
    {
        Variable vr  => vr.Type  is DataType.UINT8 or DataType.UINT16 or DataType.UINT32,
        Temporary tr => tr.Type  is DataType.UINT8 or DataType.UINT16 or DataType.UINT32,
        _            => false
    };
}
