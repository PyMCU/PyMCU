/*
 * -----------------------------------------------------------------------------
 * PyMCU — pymcu-xtensa extension
 * Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
 *
 * SPDX-License-Identifier: MIT
 * -----------------------------------------------------------------------------
 */

// Xtensa GAS assembly codegen backend for PyMCU.
//
// Targets: ESP8266 (LX106), ESP32 (LX6), ESP32-S2 (LX7), ESP32-S3 (LX7).
// ABI:     call0 (flat register model — no windowed register rotation).
//
// Register conventions (call0 ABI):
//   a0  — return address (set by call0 instruction)
//   a1  — stack pointer (grows downward)
//   a2  — first argument / return value
//   a3-a7 — arguments 2-6
//   a8  — scratch temp 0  (caller-saved)
//   a9  — scratch temp 1  (caller-saved)
//   a10 — scratch temp 2  (caller-saved)
//   a12 — callee-saved frame pointer
//
// Stack frame layout (frame size = N, 16-byte aligned):
//   SP+N-4:  saved a0 (return address)   — non-leaf functions only
//   SP+N-8:  saved a12 (frame pointer)
//   SP+N-12: first local variable
//   ...
//
// Literal pool:
//   32-bit constants that do not fit in movi's 12-bit signed range are placed
//   in the per-function literal pool using .literal.  A .literal_position
//   marker is emitted before each function so the l32r backward PC-relative
//   reference always resolves correctly.

using PyMCU.Backend.Analysis;
using PyMCU.Common.Models;
using PyMCU.IR;

namespace PyMCU.Backend.Targets.Xtensa;

public class XtensaCodeGen(DeviceConfig cfg) : CodeGen
{
    private List<XtensaAsmLine> assembly = [];
    private Dictionary<string, int> stackLayout = [];
    private int currentStackAdjustment;
    private string currentFuncName = "";
    private bool currentIsLeaf;
    private int labelCounter;
    private int litCounter;
    // Literal pool entries: label → value expression (integer string or symbol name).
    private List<(string Label, string Expr)> pendingLiterals = [];
    // Names of ProgramIR globals (scalar vars + SRAM arrays) — backed by .comm in .bss.
    private HashSet<string> _globals = [];
    // Read-only byte arrays collected from FlashData instructions; emitted into .rodata.
    private Dictionary<string, List<int>> _flashArrayPool = [];

    // Xtensa ISA immediate-field limits used across multiple methods.
    // movi/l32r selection: movi range is a signed 12-bit immediate.
    private const int MoviMinImm = -2048;
    private const int MoviMaxImm =  2047;
    // addi range is a signed 8-bit immediate.
    private const int AddiMinImm = -128;
    private const int AddiMaxImm =  127;
    // Typed-load/store offset field is an unsigned 8-bit immediate (0-255).
    private const int LoadStoreMaxOffset = 255;

    // Prefix for read-only data labels emitted into .rodata.
    // Single underscore avoids the double-underscore namespace reserved by the C standard.
    private const string RodataPrefix = "_rodata_";

    // -------------------------------------------------------------------------
    // Emit helpers
    // -------------------------------------------------------------------------

    private void Emit(string m) => assembly.Add(XtensaAsmLine.MakeInstruction(m));
    private void Emit(string m, string o1) => assembly.Add(XtensaAsmLine.MakeInstruction(m, o1));
    private void Emit(string m, string o1, string o2) => assembly.Add(XtensaAsmLine.MakeInstruction(m, o1, o2));
    private void Emit(string m, string o1, string o2, string o3) => assembly.Add(XtensaAsmLine.MakeInstruction(m, o1, o2, o3));
    private void Emit(string m, string o1, string o2, string o3, string o4) => assembly.Add(XtensaAsmLine.MakeInstruction(m, o1, o2, o3, o4));
    private void EmitLabel(string l) => assembly.Add(XtensaAsmLine.MakeLabel(l));
    private void EmitComment(string c) => assembly.Add(XtensaAsmLine.MakeComment(c));
    private void EmitRaw(string t) => assembly.Add(XtensaAsmLine.MakeRaw(t));

    private string MakeLabel(string prefix) => $"{prefix}_{labelCounter++}";

    // -------------------------------------------------------------------------
    // Immediate loading
    // -------------------------------------------------------------------------

    // Load a 32-bit constant into a register.
    // Values that fit in movi's signed 12-bit range (-2048..2047) are emitted
    // inline.  Larger values are placed in the per-function literal pool and
    // loaded with l32r (PC-relative backward reference).
    private void LoadImmediate(string reg, long value)
    {
        if (value >= MoviMinImm && value <= MoviMaxImm)
        {
            Emit("movi", reg, value.ToString());
        }
        else
        {
            string litLabel = $".Llit_{currentFuncName}_{litCounter++}";
            pendingLiterals.Add((litLabel, ((int)value).ToString()));
            Emit("l32r", reg, litLabel);
        }
    }

    // Load the address of a global symbol into a register via the literal pool.
    private void LoadSymbolAddr(string reg, string symbol)
    {
        string litLabel = $".Llit_{currentFuncName}_{litCounter++}";
        pendingLiterals.Add((litLabel, symbol));
        Emit("l32r", reg, litLabel);
    }

    // Emit a typed load: dst = *(base + offset), respecting the DataType width.
    private void EmitTypedLoad(string dst, string base_, int offset, DataType dt)
    {
        string ofs = offset.ToString();
        switch (dt)
        {
            case DataType.UINT8:
                Emit("l8ui", dst, base_, ofs);
                break;
            case DataType.INT8:
                Emit("l8ui",  dst, base_, ofs);
                Emit("sext",  dst, dst, "7");
                break;
            case DataType.UINT16:
                Emit("l16ui", dst, base_, ofs);
                break;
            case DataType.INT16:
                Emit("l16ui", dst, base_, ofs);
                Emit("sext",  dst, dst, "15");
                break;
            default:
                Emit("l32i",  dst, base_, ofs);
                break;
        }
    }

    // Emit a typed store: *(base + offset) = src, respecting the DataType width.
    private void EmitTypedStore(string src, string base_, int offset, DataType dt)
    {
        string ofs = offset.ToString();
        switch (dt)
        {
            case DataType.UINT8:
            case DataType.INT8:
                Emit("s8i",  src, base_, ofs);
                break;
            case DataType.UINT16:
            case DataType.INT16:
                Emit("s16i", src, base_, ofs);
                break;
            default:
                Emit("s32i", src, base_, ofs);
                break;
        }
    }

    // Load a Val operand into a physical register.
    private void LoadIntoReg(Val v, string reg)
    {
        switch (v)
        {
            case Constant c:
                LoadImmediate(reg, c.Value);
                break;
            case FloatConstant fc:
                LoadImmediate(reg, (long)BitConverter.SingleToUInt32Bits((float)fc.Value));
                break;
            case MemoryAddress ma:
                LoadImmediate("a10", ma.Address);
                EmitTypedLoad(reg, "a10", 0, ma.Type);
                break;
            case Variable vr:
                if (_globals.Contains(vr.Name))
                {
                    LoadSymbolAddr("a10", vr.Name.Replace('.', '_'));
                    EmitTypedLoad(reg, "a10", 0, vr.Type);
                }
                else if (stackLayout.TryGetValue(vr.Name, out int vOff))
                    Emit("l32i", reg, "a12", (-vOff).ToString());
                else
                    LoadImmediate(reg, 0);
                break;
            case Temporary tmp:
                if (stackLayout.TryGetValue(tmp.Name, out int tOff))
                    Emit("l32i", reg, "a12", (-tOff).ToString());
                else
                    LoadImmediate(reg, 0);
                break;
            case NoneVal:
                LoadImmediate(reg, 0);
                break;
        }
    }

    // Store a physical register into a Val destination.
    private void StoreRegInto(string reg, Val dst)
    {
        switch (dst)
        {
            case MemoryAddress ma:
                LoadImmediate("a10", ma.Address);
                EmitTypedStore(reg, "a10", 0, ma.Type);
                break;
            case Variable vr:
                if (_globals.Contains(vr.Name))
                {
                    LoadSymbolAddr("a10", vr.Name.Replace('.', '_'));
                    EmitTypedStore(reg, "a10", 0, vr.Type);
                }
                else if (stackLayout.TryGetValue(vr.Name, out int vOff))
                    Emit("s32i", reg, "a12", (-vOff).ToString());
                break;
            case Temporary tmp:
                if (stackLayout.TryGetValue(tmp.Name, out int tOff))
                    Emit("s32i", reg, "a12", (-tOff).ToString());
                break;
        }
    }

    // -------------------------------------------------------------------------
    // Top-level compilation entry point
    // -------------------------------------------------------------------------

    public override void Compile(ProgramIR program, TextWriter output)
    {
        assembly.Clear();
        _globals.Clear();
        _flashArrayPool.Clear();

        // Register global scalar variable names so LoadIntoReg/StoreRegInto use labels.
        foreach (var g in program.Globals)
            _globals.Add(g.Name);

        // Pre-scan function bodies: collect SRAM array definitions and flash data.
        // Arrays not in program.Globals are module-local arrays that still need BSS.
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
                        _flashArrayPool[fd.Name] = fd.Bytes;
                        break;
                }
            }
        }

        EmitComment($"Generated by pymcuc for Xtensa ({cfg.Chip})");
        EmitRaw(".option call0");

        // Extern symbol declarations.
        foreach (var sym in program.ExternSymbols)
            EmitRaw($".extern {sym}");
        if (program.ExternSymbols.Count > 0)
            EmitRaw("");

        EmitRaw(".section .text");
        EmitRaw(".align 4");

        foreach (var func in program.Functions)
            CompileFunction(func);

        var optimized = XtensaPeephole.Optimize(assembly);
        foreach (var line in optimized)
            output.WriteLine(line.ToString());

        // Emit .bss for global scalar variables and SRAM arrays.
        bool hasBss = program.Globals.Count > 0 || arrayDefs.Count > 0;
        if (hasBss)
        {
            output.WriteLine();
            output.WriteLine("\t.section .bss");
            foreach (var g in program.Globals)
            {
                if (_flashArrayPool.ContainsKey(g.Name)) continue;
                var safeName = g.Name.Replace('.', '_');
                int size  = g.Type.SizeOf();
                int align = size > 4 ? 4 : size;
                output.WriteLine($"\t.comm {safeName}, {size}, {align}");
            }
            foreach (var (name, (elemType, count)) in arrayDefs)
            {
                var safeName  = name.Replace('.', '_');
                int elemSize  = elemType.SizeOf();
                int totalSize = count * elemSize;
                int align     = elemSize > 4 ? 4 : elemSize;
                output.WriteLine($"\t.comm {safeName}, {totalSize}, {align}");
            }
        }

        // Emit .rodata for flash/read-only byte arrays.
        if (_flashArrayPool.Count > 0)
        {
            output.WriteLine();
            output.WriteLine("\t.section .rodata");
            output.WriteLine("\t.align 4");
            foreach (var (name, bytes) in _flashArrayPool)
            {
                var safeName = RodataPrefix + name.Replace('.', '_');
                output.WriteLine($"\t.global {safeName}");
                output.WriteLine($"{safeName}:");
                output.WriteLine($"\t.byte {string.Join(", ", bytes)}");
                output.WriteLine($"\t.balign 4");
            }
        }
    }

    public override void EmitContextSave() { }
    public override void EmitContextRestore() { }
    public override void EmitInterruptReturn() { }

    // -------------------------------------------------------------------------
    // Function compilation
    // -------------------------------------------------------------------------

    private void CompileFunction(Function func)
    {
        pendingLiterals.Clear();
        litCounter = 0;
        currentFuncName = func.Name;
        currentIsLeaf = true;

        foreach (var instr in func.Body)
        {
            if (instr is Call) { currentIsLeaf = false; break; }
        }

        var allocator = new DynamicStackAllocator();
        var (offsets, frameSize) = allocator.Allocate(func);
        stackLayout = offsets;
        currentStackAdjustment = frameSize;

        // Two-pass: compile body into a temporary list so we can prepend the
        // literal pool (populated during body compilation) before the function.
        var savedAssembly = assembly;
        assembly = [];

        EmitRaw($".global {func.Name}");
        EmitRaw($".type {func.Name}, @function");
        EmitRaw(".align 4");
        EmitLabel(func.Name);

        // For the bare-metal entry point, initialise the stack pointer to the
        // top of DRAM before the standard prologue adjusts it.
        // DRAM tops (inclusive):
        //   ESP8266 / LX106  — 0x40000000 (96 KB DRAM)
        //   ESP32-S3          — 0x3FCFFFFF (512 KB DRAM)
        //   ESP32 / ESP32-S2  — 0x3FFFFFFF (320 KB DRAM)
        if (func.Name == "main")
        {
            string stackTop = cfg.Chip.ToLowerInvariant() switch
            {
                var c when c.StartsWith("esp8266") || c == "lx106" => "0x40000000",
                var c when c.StartsWith("esp32s3") || c.StartsWith("esp32-s3") => "0x3FCFFFFF",
                _ => "0x3FFFFFFF"   // esp32, esp32s2, esp32-s2, generic
            };
            EmitComment("initialise stack pointer to top of DRAM");
            Emit("movi", "a1", stackTop);
        }

        // Prologue: allocate frame, save return address (non-leaf) and FP.
        Emit("addi", "a1", "a1", (-currentStackAdjustment).ToString());
        if (!currentIsLeaf)
            Emit("s32i", "a0", "a1", (currentStackAdjustment - 4).ToString());
        Emit("s32i", "a12", "a1", (currentStackAdjustment - 8).ToString());
        Emit("addi", "a12", "a1", currentStackAdjustment.ToString());

        foreach (var instr in func.Body)
            CompileInstruction(instr);

        var funcLines = assembly;
        assembly = savedAssembly;

        // Prepend .literal_position and any collected literals before the body.
        if (pendingLiterals.Count > 0)
        {
            EmitRaw(".literal_position");
            foreach (var (label, expr) in pendingLiterals)
                EmitRaw($".literal {label}, {expr}");
        }

        foreach (var line in funcLines)
            assembly.Add(line);

        EmitRaw($".size {func.Name}, . - {func.Name}");
    }

    // -------------------------------------------------------------------------
    // Instruction dispatch
    // -------------------------------------------------------------------------

    // Return true when a Val represents an unsigned integer type.
    private static bool IsUnsignedVal(Val v) => v switch
    {
        Variable vr  => vr.Type  is DataType.UINT8 or DataType.UINT16 or DataType.UINT32,
        Temporary tr => tr.Type  is DataType.UINT8 or DataType.UINT16 or DataType.UINT32,
        _            => false
    };

    private void CompileInstruction(Instruction instr)
    {
        switch (instr)
        {
            case Copy arg:           CompileCopy(arg); break;
            case Return arg:         CompileReturn(arg); break;
            case Jump arg:           Emit("j", arg.Target); break;
            case JumpIfZero arg:     LoadIntoReg(arg.Condition, "a8"); Emit("beqz", "a8", arg.Target); break;
            case JumpIfNotZero arg:  LoadIntoReg(arg.Condition, "a8"); Emit("bnez", "a8", arg.Target); break;
            case JumpIfEqual arg:
                LoadIntoReg(arg.Src1, "a8"); LoadIntoReg(arg.Src2, "a9");
                Emit("beq", "a8", "a9", arg.Target); break;
            case JumpIfNotEqual arg:
                LoadIntoReg(arg.Src1, "a8"); LoadIntoReg(arg.Src2, "a9");
                Emit("bne", "a8", "a9", arg.Target); break;
            case JumpIfLessThan arg:
                LoadIntoReg(arg.Src1, "a8"); LoadIntoReg(arg.Src2, "a9");
                Emit(IsUnsignedVal(arg.Src1) || IsUnsignedVal(arg.Src2) ? "bltu" : "blt",
                     "a8", "a9", arg.Target); break;
            case JumpIfLessOrEqual arg:
                // a8 <= a9  ↔  a9 >= a8
                LoadIntoReg(arg.Src1, "a8"); LoadIntoReg(arg.Src2, "a9");
                Emit(IsUnsignedVal(arg.Src1) || IsUnsignedVal(arg.Src2) ? "bgeu" : "bge",
                     "a9", "a8", arg.Target); break;
            case JumpIfGreaterThan arg:
                // a8 > a9  ↔  a9 < a8
                LoadIntoReg(arg.Src1, "a8"); LoadIntoReg(arg.Src2, "a9");
                Emit(IsUnsignedVal(arg.Src1) || IsUnsignedVal(arg.Src2) ? "bltu" : "blt",
                     "a9", "a8", arg.Target); break;
            case JumpIfGreaterOrEqual arg:
                // a8 >= a9
                LoadIntoReg(arg.Src1, "a8"); LoadIntoReg(arg.Src2, "a9");
                Emit(IsUnsignedVal(arg.Src1) || IsUnsignedVal(arg.Src2) ? "bgeu" : "bge",
                     "a8", "a9", arg.Target); break;
            case JumpIfBitSet arg:
                LoadIntoReg(arg.Source, "a8");
                Emit("bbsi", "a8", arg.Bit.ToString(), arg.Target); break;
            case JumpIfBitClear arg:
                LoadIntoReg(arg.Source, "a8");
                Emit("bbci", "a8", arg.Bit.ToString(), arg.Target); break;
            case Label arg:          EmitLabel(arg.Name); break;
            case Call arg:           CompileCall(arg); break;
            case Unary arg:          CompileUnary(arg); break;
            case Binary arg:         CompileBinary(arg); break;
            case BitSet arg:         CompileBitSet(arg); break;
            case BitClear arg:       CompileBitClear(arg); break;
            case BitCheck arg:       CompileBitCheck(arg); break;
            case BitWrite arg:       CompileBitWrite(arg); break;
            case InlineAsm arg:      assembly.Add(XtensaAsmLine.MakeRaw(arg.Code)); break;
            case DebugLine arg:
                if (!string.IsNullOrEmpty(arg.SourceFile))
                    EmitComment($"{arg.SourceFile}:{arg.Line}: {arg.Text}");
                else
                    EmitComment($"Line {arg.Line}: {arg.Text}");
                break;
            case AugAssign arg:      CompileAugAssign(arg); break;
            case LoadIndirect arg:   CompileLoadIndirect(arg); break;
            case StoreIndirect arg:  CompileStoreIndirect(arg); break;
            case ArrayLoad arg:      CompileArrayLoad(arg); break;
            case ArrayStore arg:     CompileArrayStore(arg); break;
            case ArrayLoadFlash arg: CompileArrayLoadFlash(arg); break;
            case FlashData:          break; // collected in Compile() pre-scan
        }
    }

    // -------------------------------------------------------------------------
    // Return
    // -------------------------------------------------------------------------

    private void CompileReturn(Return arg)
    {
        if (arg.Value is not NoneVal)
            LoadIntoReg(arg.Value, "a2");

        if (currentFuncName == "main")
        {
            // Bare-metal entry: spin forever after the program exits.
            string endLabel = MakeLabel("end_loop");
            EmitLabel(endLabel);
            Emit("j", endLabel);
        }
        else
        {
            if (!currentIsLeaf)
                Emit("l32i", "a0", "a1", (currentStackAdjustment - 4).ToString());
            Emit("l32i", "a12", "a1", (currentStackAdjustment - 8).ToString());
            Emit("addi", "a1", "a1", currentStackAdjustment.ToString());
            Emit("ret.n");
        }
    }

    // -------------------------------------------------------------------------
    // Copy
    // -------------------------------------------------------------------------

    private void CompileCopy(Copy arg)
    {
        LoadIntoReg(arg.Src, "a8");
        StoreRegInto("a8", arg.Dst);
    }

    // -------------------------------------------------------------------------
    // Call
    // -------------------------------------------------------------------------

    private void CompileCall(Call arg)
    {
        // Pass arguments in a2–a7 (call0 ABI: up to 6 arguments).
        string[] argRegs = ["a2", "a3", "a4", "a5", "a6", "a7"];
        int i = 0;
        foreach (var a in arg.Args)
        {
            if (i >= argRegs.Length) break;
            LoadIntoReg(a, argRegs[i++]);
        }

        Emit("call0", arg.FunctionName);
        StoreRegInto("a2", arg.Dst);
    }

    // -------------------------------------------------------------------------
    // Unary
    // -------------------------------------------------------------------------

    private void CompileUnary(Unary arg)
    {
        LoadIntoReg(arg.Src, "a8");
        switch (arg.Op)
        {
            case UnaryOp.Neg:
                Emit("neg", "a8", "a8");
                break;
            case UnaryOp.BitNot:
                Emit("bnot", "a8", "a8");
                break;
            case UnaryOp.Not:
            {
                // logical not: result = (a8 == 0) ? 1 : 0
                string trueLabel = MakeLabel("lnot_one");
                string doneLabel = MakeLabel("lnot_done");
                Emit("beqz", "a8", trueLabel);
                Emit("movi", "a8", "0");
                Emit("j", doneLabel);
                EmitLabel(trueLabel);
                Emit("movi", "a8", "1");
                EmitLabel(doneLabel);
                break;
            }
        }
        StoreRegInto("a8", arg.Dst);
    }

    // -------------------------------------------------------------------------
    // Binary
    // -------------------------------------------------------------------------

    private void CompileBinary(Binary arg)
    {
        LoadIntoReg(arg.Src1, "a8");

        // Use immediate-form instructions where possible (small constant RHS).
        bool usedImmediate = false;
        if (arg.Src2 is Constant c2 && c2.Value >= AddiMinImm && c2.Value <= AddiMaxImm)
        {
            int val = c2.Value;
            switch (arg.Op)
            {
                case BinaryOp.Add:
                    Emit("addi", "a8", "a8", val.ToString());
                    usedImmediate = true;
                    break;
                case BinaryOp.Sub:
                    Emit("addi", "a8", "a8", (-val).ToString());
                    usedImmediate = true;
                    break;
            }
        }

        if (!usedImmediate)
        {
            LoadIntoReg(arg.Src2, "a9");
            bool isUnsigned = IsUnsignedVal(arg.Src1) || IsUnsignedVal(arg.Src2);
            switch (arg.Op)
            {
                case BinaryOp.Add:      Emit("add",  "a8", "a8", "a9"); break;
                case BinaryOp.Sub:      Emit("sub",  "a8", "a8", "a9"); break;
                case BinaryOp.BitAnd:   Emit("and",  "a8", "a8", "a9"); break;
                case BinaryOp.BitOr:    Emit("or",   "a8", "a8", "a9"); break;
                case BinaryOp.BitXor:   Emit("xor",  "a8", "a8", "a9"); break;
                case BinaryOp.LShift:   Emit("ssl",  "a9"); Emit("sll", "a8", "a8"); break;
                case BinaryOp.RShift:
                    // srl = logical (unsigned), sra = arithmetic (signed).
                    Emit("ssr", "a9");
                    Emit(isUnsigned ? "srl" : "sra", "a8", "a8");
                    break;
                case BinaryOp.Mul:
                    Emit("mov", "a2", "a8"); Emit("mov", "a3", "a9");
                    Emit("call0", "__mulsi3"); Emit("mov", "a8", "a2");
                    break;
                case BinaryOp.Div:
                case BinaryOp.FloorDiv:
                    Emit("mov", "a2", "a8"); Emit("mov", "a3", "a9");
                    Emit("call0", isUnsigned ? "__udivsi3" : "__divsi3");
                    Emit("mov", "a8", "a2");
                    break;
                case BinaryOp.Mod:
                    Emit("mov", "a2", "a8"); Emit("mov", "a3", "a9");
                    Emit("call0", isUnsigned ? "__umodsi3" : "__modsi3");
                    Emit("mov", "a8", "a2");
                    break;
                case BinaryOp.Equal:
                    Emit("xor", "a8", "a8", "a9");
                    CompileSeqz("a8");
                    break;
                case BinaryOp.NotEqual:
                    Emit("xor", "a8", "a8", "a9");
                    CompileSnez("a8");
                    break;
                case BinaryOp.LessThan:
                    if (isUnsigned) CompileSltu("a8", "a8", "a9");
                    else            CompileSlt("a8",  "a8", "a9");
                    break;
                case BinaryOp.GreaterEqual:
                    // >= is !(a8 < a9)
                    if (isUnsigned) CompileSltu("a8", "a8", "a9");
                    else            CompileSlt("a8",  "a8", "a9");
                    CompileSeqz("a8");
                    break;
                case BinaryOp.GreaterThan:
                    // a8 > a9  ↔  a9 < a8
                    if (isUnsigned) CompileSltu("a8", "a9", "a8");
                    else            CompileSlt("a8",  "a9", "a8");
                    break;
                case BinaryOp.LessEqual:
                    // a8 <= a9  ↔  !(a9 < a8)
                    if (isUnsigned) CompileSltu("a8", "a9", "a8");
                    else            CompileSlt("a8",  "a9", "a8");
                    CompileSeqz("a8");
                    break;
            }
        }

        StoreRegInto("a8", arg.Dst);
    }

    // Xtensa has no seqz/snez/slt pseudo-instructions; emulate with branches.
    private void CompileSeqz(string reg)
    {
        string tl = MakeLabel("seqz_t");
        string dl = MakeLabel("seqz_d");
        Emit("beqz", reg, tl);
        Emit("movi", reg, "0");
        Emit("j", dl);
        EmitLabel(tl);
        Emit("movi", reg, "1");
        EmitLabel(dl);
    }

    private void CompileSnez(string reg)
    {
        string tl = MakeLabel("snez_t");
        string dl = MakeLabel("snez_d");
        Emit("bnez", reg, tl);
        Emit("movi", reg, "0");
        Emit("j", dl);
        EmitLabel(tl);
        Emit("movi", reg, "1");
        EmitLabel(dl);
    }

    // signed less-than: dst = (src1 < src2) ? 1 : 0
    private void CompileSlt(string dst, string src1, string src2)
    {
        string tl = MakeLabel("slt_t");
        string dl = MakeLabel("slt_d");
        Emit("blt", src1, src2, tl);
        Emit("movi", dst, "0");
        Emit("j", dl);
        EmitLabel(tl);
        Emit("movi", dst, "1");
        EmitLabel(dl);
    }

    // unsigned less-than: dst = (src1 <u src2) ? 1 : 0
    private void CompileSltu(string dst, string src1, string src2)
    {
        string tl = MakeLabel("sltu_t");
        string dl = MakeLabel("sltu_d");
        Emit("bltu", src1, src2, tl);
        Emit("movi", dst, "0");
        Emit("j", dl);
        EmitLabel(tl);
        Emit("movi", dst, "1");
        EmitLabel(dl);
    }

    // -------------------------------------------------------------------------
    // AugAssign  (+=, -=, *=, etc. on SRAM variables and array elements)
    // -------------------------------------------------------------------------

    private void CompileAugAssign(AugAssign arg)
    {
        LoadIntoReg(arg.Target, "a8");

        bool usedImmediate = false;
        if (arg.Operand is Constant c2 && c2.Value >= AddiMinImm && c2.Value <= AddiMaxImm)
        {
            int val = c2.Value;
            switch (arg.Op)
            {
                case BinaryOp.Add:
                    Emit("addi", "a8", "a8", val.ToString());
                    usedImmediate = true;
                    break;
                case BinaryOp.Sub:
                    Emit("addi", "a8", "a8", (-val).ToString());
                    usedImmediate = true;
                    break;
            }
        }

        if (!usedImmediate)
        {
            LoadIntoReg(arg.Operand, "a9");
            bool isUnsigned = IsUnsignedVal(arg.Target) || IsUnsignedVal(arg.Operand);
            switch (arg.Op)
            {
                case BinaryOp.Add:      Emit("add",  "a8", "a8", "a9"); break;
                case BinaryOp.Sub:      Emit("sub",  "a8", "a8", "a9"); break;
                case BinaryOp.BitAnd:   Emit("and",  "a8", "a8", "a9"); break;
                case BinaryOp.BitOr:    Emit("or",   "a8", "a8", "a9"); break;
                case BinaryOp.BitXor:   Emit("xor",  "a8", "a8", "a9"); break;
                case BinaryOp.LShift:   Emit("ssl",  "a9"); Emit("sll", "a8", "a8"); break;
                case BinaryOp.RShift:
                    Emit("ssr", "a9");
                    Emit(isUnsigned ? "srl" : "sra", "a8", "a8");
                    break;
                case BinaryOp.Mul:
                    Emit("mov", "a2", "a8"); Emit("mov", "a3", "a9");
                    Emit("call0", "__mulsi3"); Emit("mov", "a8", "a2");
                    break;
                case BinaryOp.Div:
                case BinaryOp.FloorDiv:
                    Emit("mov", "a2", "a8"); Emit("mov", "a3", "a9");
                    Emit("call0", isUnsigned ? "__udivsi3" : "__divsi3");
                    Emit("mov", "a8", "a2");
                    break;
                case BinaryOp.Mod:
                    Emit("mov", "a2", "a8"); Emit("mov", "a3", "a9");
                    Emit("call0", isUnsigned ? "__umodsi3" : "__modsi3");
                    Emit("mov", "a8", "a2");
                    break;
            }
        }

        StoreRegInto("a8", arg.Target);
    }

    // -------------------------------------------------------------------------
    // LoadIndirect / StoreIndirect  (pointer dereference — MMIO, ptr[T])
    // -------------------------------------------------------------------------

    private void CompileLoadIndirect(LoadIndirect arg)
    {
        LoadIntoReg(arg.SrcPtr, "a10");
        var dt = arg.Dst is Variable vr ? vr.Type
               : arg.Dst is Temporary tr ? tr.Type
               : DataType.UINT32;
        EmitTypedLoad("a8", "a10", 0, dt);
        StoreRegInto("a8", arg.Dst);
    }

    private void CompileStoreIndirect(StoreIndirect arg)
    {
        LoadIntoReg(arg.Src, "a8");
        LoadIntoReg(arg.DstPtr, "a10");
        var dt = arg.Src is Variable sv ? sv.Type
               : arg.Src is Temporary st ? st.Type
               : DataType.UINT32;
        EmitTypedStore("a8", "a10", 0, dt);
    }

    // -------------------------------------------------------------------------
    // ArrayLoad / ArrayStore  (SRAM variable-index arrays)
    // -------------------------------------------------------------------------

    // Scale idxReg by elemSize and add to baseReg; result in baseReg.
    // Uses addx2/addx4/addx8 Xtensa instructions for power-of-two element sizes.
    private void ScaleAndAddIndex(string baseReg, string idxReg, int elemSize)
    {
        switch (elemSize)
        {
            case 1: Emit("add",   baseReg, baseReg, idxReg); break;
            // addx2/4/8  ar, as, at  →  ar = (as << n) + at
            case 2: Emit("addx2", baseReg, idxReg,  baseReg); break;
            case 4: Emit("addx4", baseReg, idxReg,  baseReg); break;
            case 8: Emit("addx8", baseReg, idxReg,  baseReg); break;
            default:
                // Uncommon element size — fall back to software multiply.
                Emit("mov", "a2", idxReg);
                LoadImmediate("a3", elemSize);
                Emit("call0", "__mulsi3");
                Emit("add", baseReg, baseReg, "a2");
                break;
        }
    }

    // Load the base address of an array into a10 (global label or frame-relative).
    // Returns false and emits a comment if the array is not found.
    private bool LoadArrayBase(string arrayName, string baseReg)
    {
        if (_globals.Contains(arrayName))
        {
            LoadSymbolAddr(baseReg, arrayName.Replace('.', '_'));
            return true;
        }
        if (stackLayout.TryGetValue(arrayName, out int baseOff))
        {
            int adjusted = -baseOff;
            if (adjusted >= AddiMinImm && adjusted <= AddiMaxImm)
                Emit("addi", baseReg, "a12", adjusted.ToString());
            else
            {
                LoadImmediate(baseReg, adjusted);
                Emit("add", baseReg, "a12", baseReg);
            }
            return true;
        }
        EmitComment($"array '{arrayName}' not found in layout -- skip");
        return false;
    }

    private void CompileArrayLoad(ArrayLoad al)
    {
        int elemSize = al.ElemType.SizeOf();

        if (al.Index is Constant cidx)
        {
            // Constant-index path: load base, then emit typed load with byte offset.
            if (!LoadArrayBase(al.ArrayName, "a10")) { LoadImmediate("a8", 0); StoreRegInto("a8", al.Dst); return; }
            int byteOff = cidx.Value * elemSize;
            if (byteOff >= 0 && byteOff <= LoadStoreMaxOffset)
            {
                EmitTypedLoad("a8", "a10", byteOff, al.ElemType);
            }
            else
            {
                LoadImmediate("a9", byteOff);
                Emit("add", "a10", "a10", "a9");
                EmitTypedLoad("a8", "a10", 0, al.ElemType);
            }
        }
        else
        {
            // Runtime-index path: load index first so a10 is free for base.
            LoadIntoReg(al.Index, "a9");
            if (!LoadArrayBase(al.ArrayName, "a10")) { LoadImmediate("a8", 0); StoreRegInto("a8", al.Dst); return; }
            ScaleAndAddIndex("a10", "a9", elemSize);
            EmitTypedLoad("a8", "a10", 0, al.ElemType);
        }

        StoreRegInto("a8", al.Dst);
    }

    private void CompileArrayStore(ArrayStore ast)
    {
        int elemSize = ast.ElemType.SizeOf();

        if (ast.Index is Constant cidx)
        {
            // Constant-index path.
            LoadIntoReg(ast.Src, "a8");
            if (!LoadArrayBase(ast.ArrayName, "a10")) return;
            int byteOff = cidx.Value * elemSize;
            if (byteOff >= 0 && byteOff <= LoadStoreMaxOffset)
            {
                EmitTypedStore("a8", "a10", byteOff, ast.ElemType);
            }
            else
            {
                Emit("mov", "a11", "a8");           // save value in a11
                LoadImmediate("a8", byteOff);
                Emit("add", "a10", "a10", "a8");
                EmitTypedStore("a11", "a10", 0, ast.ElemType);
            }
        }
        else
        {
            // Runtime-index path.
            // Load source into a11 first so it survives the address calculation.
            LoadIntoReg(ast.Src, "a11");
            // Load index into a9.  LoadIntoReg may internally use a10 as a scratch
            // register when loading a global variable, but that is fine: a10 is not
            // yet carrying useful data at this point, and a9 will hold the final
            // index value after the call returns.
            LoadIntoReg(ast.Index, "a9");
            // Compute array base address into a10 (overwrites any earlier a10 scratch use).
            if (!LoadArrayBase(ast.ArrayName, "a10")) return;
            ScaleAndAddIndex("a10", "a9", elemSize);
            EmitTypedStore("a11", "a10", 0, ast.ElemType);
        }
    }

    // -------------------------------------------------------------------------
    // ArrayLoadFlash  (read-only .rodata byte arrays)
    // -------------------------------------------------------------------------

    private void CompileArrayLoadFlash(ArrayLoadFlash alf)
    {
        // The table is placed in .rodata under the label "_rodata_<safeName>".
        var label = RodataPrefix + alf.ArrayName.Replace('.', '_');
        // Load index first; base address will then be loaded into a10.
        LoadIntoReg(alf.Index, "a9");
        LoadSymbolAddr("a10", label);
        Emit("add",  "a10", "a10", "a9");
        Emit("l8ui", "a8",  "a10", "0");
        StoreRegInto("a8", alf.Dst);
    }

    // -------------------------------------------------------------------------
    // Bit operations
    // -------------------------------------------------------------------------

    private void CompileBitSet(BitSet arg)
    {
        LoadIntoReg(arg.Target, "a8");
        Emit("movi", "a9", (1 << arg.Bit).ToString());
        Emit("or", "a8", "a8", "a9");
        StoreRegInto("a8", arg.Target);
    }

    private void CompileBitClear(BitClear arg)
    {
        LoadIntoReg(arg.Target, "a8");
        Emit("movi", "a9", (~(1 << arg.Bit)).ToString());
        Emit("and", "a8", "a8", "a9");
        StoreRegInto("a8", arg.Target);
    }

    private void CompileBitCheck(BitCheck arg)
    {
        LoadIntoReg(arg.Source, "a8");
        // extui: extract bit field (dst, src, low-bit, bitcount)
        Emit("extui", "a8", "a8", arg.Bit.ToString(), "1");
        StoreRegInto("a8", arg.Dst);
    }

    private void CompileBitWrite(BitWrite arg)
    {
        LoadIntoReg(arg.Src, "a8");
        LoadIntoReg(arg.Target, "a9");
        // Clear the target bit.
        Emit("movi", "a10", (~(1 << arg.Bit)).ToString());
        Emit("and", "a9", "a9", "a10");
        // Normalise src to 0 or 1.
        CompileSnez("a8");
        // Shift src into position.
        Emit("movi", "a10", arg.Bit.ToString());
        Emit("ssl", "a10");
        Emit("sll", "a8", "a8");
        // Merge.
        Emit("or", "a9", "a9", "a8");
        StoreRegInto("a9", arg.Target);
    }
}
