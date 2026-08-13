/*
 * -----------------------------------------------------------------------------
 * PyMCU Compiler (pymcuc)
 * Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
 *
 * SPDX-License-Identifier: MIT
 *
 * -----------------------------------------------------------------------------
 * SAFETY WARNING / HIGH RISK ACTIVITIES:
 * THE SOFTWARE IS NOT DESIGNED, MANUFACTURED, OR INTENDED FOR USE IN HAZARDOUS
 * ENVIRONMENTS REQUIRING FAIL-SAFE PERFORMANCE, SUCH AS IN THE OPERATION OF
 * NUCLEAR FACILITIES, AIRCRAFT NAVIGATION OR COMMUNICATION SYSTEMS, AIR
 * TRAFFIC CONTROL, DIRECT LIFE SUPPORT MACHINES, OR WEAPONS SYSTEMS.
 * -----------------------------------------------------------------------------
 */

using PyMCU.Backend.Analysis;
using PyMCU.Common;
using PyMCU.Common.Models;
using PyMCU.IR;

namespace PyMCU.Backend.Targets.RiscV;

public partial class RiscvCodeGen(DeviceConfig cfg) : CodeGen
{
    // Argument/return registers of the ilp32e ABI. RV32E halves the register file,
    // so only a0-a5 exist — x16 and above are not encodable.
    private static readonly string[] ArgRegs = ["a0", "a1", "a2", "a3", "a4", "a5"];

    // Error-propagation register, the counterpart of AVR's T flag: zero means the
    // happy path, non-zero carries the error code up to the caller. Nothing else
    // in the backend touches s1 and no prologue saves it, so it behaves like the
    // global condition flag the architecture-agnostic error model expects.
    private const string ErrorReg = "s1";

    private List<RISCVAsmLine> assembly = new();
    private Dictionary<string, int> stackLayout = new();
    private readonly HashSet<string> globalNames = new();
    private int currentStackAdjustment;
    private string currentFuncName = "";
    private bool currentIsLeaf;
    private bool currentIsInterrupt;
    private int labelCounter;

    // Caller-saved registers an ISR must preserve, because the interrupted code
    // holds live values in them. ra is not here: the ordinary prologue already
    // saves it whenever the function is not a leaf, and a leaf ISR never writes
    // it. s0 is likewise handled by the ordinary prologue.
    private static readonly string[] IsrSavedRegs =
        ["t0", "t1", "t2", "a0", "a1", "a2", "a3", "a4", "a5", ErrorReg];

    public override void EmitContextSave()
    {
        EmitStackAdjust(-IsrSavedRegs.Length * 4);
        for (int i = 0; i < IsrSavedRegs.Length; i++)
            Emit("sw", IsrSavedRegs[i], $"{i * 4}(sp)");
    }

    public override void EmitContextRestore()
    {
        for (int i = 0; i < IsrSavedRegs.Length; i++)
            Emit("lw", IsrSavedRegs[i], $"{i * 4}(sp)");
        EmitStackAdjust(IsrSavedRegs.Length * 4);
    }

    public override void EmitInterruptReturn() => Emit("mret");

    private string MakeLabel(string prefix) => $"{prefix}_{labelCounter++}";

    private void Emit(string m) => assembly.Add(RISCVAsmLine.MakeInstruction(m));
    private void Emit(string m, string o1) => assembly.Add(RISCVAsmLine.MakeInstruction(m, o1));
    private void Emit(string m, string o1, string o2) => assembly.Add(RISCVAsmLine.MakeInstruction(m, o1, o2));
    private void Emit(string m, string o1, string o2, string o3) => assembly.Add(RISCVAsmLine.MakeInstruction(m, o1, o2, o3));
    private void EmitLabel(string l) => assembly.Add(RISCVAsmLine.MakeLabel(l));
    private void EmitComment(string c) => assembly.Add(RISCVAsmLine.MakeComment(c));
    private void EmitRaw(string t) => assembly.Add(RISCVAsmLine.MakeRaw(t));

    private static string ValName(Val val)
        => val is Variable vr ? vr.Name : val is Temporary tr ? tr.Name : "";

    private static bool IsSignedType(DataType t)
        => t is DataType.INT8 or DataType.INT16 or DataType.INT32;

    private static DataType TypeOf(Val val) => val switch
    {
        Variable v => v.Type,
        Temporary t => t.Type,
        MemoryAddress m => m.Type,
        _ => DataType.UNKNOWN,
    };

    // Whether a division runs in a signed context, mirroring the AVR backend: a
    // negative literal implies signedness even when constant folding lost the
    // type. Unsigned division cannot use the signed routines, because an operand
    // above 2^31 would read as negative.
    private static bool IsSignedContext(Val a, Val b)
    {
        if (IsSignedType(TypeOf(a)) || IsSignedType(TypeOf(b))) return true;
        if (a is Constant ca && ca.Value < 0) return true;
        if (b is Constant cb && cb.Value < 0) return true;
        return false;
    }

    // Load/store mnemonics for an access of the given element width. Narrow
    // signed types sign-extend (lb/lh) so a negative int8 keeps its value in a
    // 32-bit register; unsigned ones zero-extend (lbu/lhu). Stores only care
    // about the width.
    private static string LoadMnemonic(DataType type) => type switch
    {
        DataType.INT8 => "lb",
        DataType.UINT8 => "lbu",
        DataType.INT16 => "lh",
        DataType.UINT16 => "lhu",
        _ => "lw",
    };

    private static string StoreMnemonic(DataType type) => type.SizeOf() switch
    {
        1 => "sb",
        2 => "sh",
        _ => "sw",
    };

    private void LoadIntoReg(Val val, string reg)
    {
        switch (val)
        {
            case Constant c:
                Emit("li", reg, c.Value.ToString());
                return;
            case MemoryAddress mem:
                Emit("li", "t2", $"0x{mem.Address:X8}");
                Emit("lw", reg, "0(t2)");
                return;
            case FunctionRef fr:
                Emit("la", reg, fr.FunctionName);
                return;
            case ArrayBase ab:
                Emit("la", reg, ab.ArrayName);
                return;
            case FlashStrAddr fs:
                Emit("la", reg, FlashSymbol(fs.Name));
                return;
            case NoneVal:
                Emit("li", reg, "0");
                return;
        }

        string name = ValName(val);
        if (string.IsNullOrEmpty(name))
            throw new NotSupportedException(
                $"RISC-V backend: cannot load operand of type {val.GetType().Name}.");

        if (stackLayout.TryGetValue(name, out int offset))
            Emit("lw", reg, $"{offset}(s0)");
        else
        {
            Emit("la", "t2", name);
            Emit("lw", reg, "0(t2)");
        }
    }

    private void StoreRegInto(string reg, Val val)
    {
        if (val is Constant or NoneVal) return;

        if (val is MemoryAddress mem)
        {
            Emit("li", "t2", $"0x{mem.Address:X8}");
            Emit("sw", reg, "0(t2)");
            return;
        }

        string name = ValName(val);
        if (string.IsNullOrEmpty(name))
            throw new NotSupportedException(
                $"RISC-V backend: cannot store into operand of type {val.GetType().Name}.");

        if (stackLayout.TryGetValue(name, out int offset))
            Emit("sw", reg, $"{offset}(s0)");
        else
        {
            Emit("la", "t2", name);
            Emit("sw", reg, "0(t2)");
        }
    }

    public override void Compile(ProgramIR program, TextWriter output)
    {
        assembly.Clear();
        globalNames.Clear();
        flashTables.Clear();
        arrayBytes.Clear();
        foreach (var g in program.Globals) globalNames.Add(g.Name);
        foreach (var (name, count) in program.GlobalArrays)
        {
            globalNames.Add(name);
            // GlobalArrays only carries a count; assume word elements until an
            // indexed access tells us the real element type.
            NoteArray(name, count, DataType.UINT32);
        }

        ScanForRuntimeHelpers(program);

        EmitComment($"Generated by pymcuc for RISC-V ({cfg.Chip})");
        // Declared in the file so it assembles without -march. Zicsr is needed
        // by the startup code, which programs mtvec/mepc.
        EmitRaw($".attribute arch, \"{Profile.Isa}\"");
        EmitRaw(".attribute unaligned_access, 0");
        EmitRaw($".attribute stack_align, {Profile.StackAlign}");

        EmitStartup(program);

        EmitRaw(".section .text");
        EmitRaw(".align 2");

        foreach (var func in program.Functions)
            CompileFunction(func);

        EmitRuntimeHelpers();
        EmitDataSections(program);

        var optimized = RiscvPeephole.Optimize(assembly);
        foreach (var line in optimized)
            output.WriteLine(line.ToString());
    }

    private void CompileFunction(Function func)
    {
        currentFuncName = func.Name;
        currentIsLeaf = IsLeaf(func);
        currentIsInterrupt = func.IsInterrupt;

        var allocator = new DynamicStackAllocator();
        var (offsets, frameSize) = allocator.Allocate(func);
        stackLayout = offsets;

        // Module-level variables live in .data/.bss, not in the frame. The shared
        // allocator has no notion of globals, so their slots are dropped here and
        // the operand helpers fall through to the `la <symbol>` path.
        foreach (var name in globalNames)
            stackLayout.Remove(name);

        // Any name the allocator missed (it does not walk every instruction shape)
        // still needs a home, otherwise it would silently become an undefined symbol.
        AllocateMissingSlots(func, ref frameSize);
        currentStackAdjustment = frameSize;

        EmitRaw(".globl " + func.Name);
        EmitLabel(func.Name);

        // An ISR interrupts arbitrary code, so the scratch registers have to be
        // preserved before the ordinary frame is even set up.
        if (currentIsInterrupt)
            EmitContextSave();

        // Prologue
        EmitStackAdjust(-currentStackAdjustment);
        if (!currentIsLeaf)
            Emit("sw", "ra", $"{currentStackAdjustment - 4}(sp)");
        Emit("sw", "s0", $"{currentStackAdjustment - 8}(sp)");
        EmitFramePointer();

        // Spill incoming arguments into the slots the body reads them from.
        for (int i = 0; i < func.Params.Count; i++)
        {
            if (i >= ArgRegs.Length)
                throw new NotSupportedException(
                    $"RISC-V backend: '{func.Name}' takes {func.Params.Count} parameters; " +
                    $"the ilp32e ABI passes at most {ArgRegs.Length} in registers and " +
                    "stack-passed arguments are not implemented yet.");

            if (stackLayout.TryGetValue(func.Params[i], out int offset))
                Emit("sw", ArgRegs[i], $"{offset}(s0)");
        }

        foreach (var instr in func.Body)
            CompileInstruction(instr);
    }

    // A leaf keeps ra untouched, so the prologue can skip saving it. Getting
    // this wrong in the optimistic direction corrupts the return address, hence
    // every operation that may lower to a call counts.
    private bool IsLeaf(Function func)
    {
        foreach (var instr in func.Body)
        {
            switch (instr)
            {
                case Call:
                case IndirectCall:
                    return false;
                case Binary bin when CallsHelper(bin.Op):
                    return false;
                case AugAssign aug when CallsHelper(aug.Op):
                    return false;
            }
        }

        return true;
    }

    private bool CallsHelper(PyMCU.IR.BinaryOp op) => op switch
    {
        // Division only stays in registers when the operands are unsigned and
        // the core divides in hardware. That depends on the operand types, which
        // this check does not see, so it assumes the call happens: over-saving ra
        // costs a store, under-saving it corrupts the return address.
        PyMCU.IR.BinaryOp.Div or PyMCU.IR.BinaryOp.FloorDiv or PyMCU.IR.BinaryOp.Mod => true,
        PyMCU.IR.BinaryOp.Mul => !Profile.HasMulDiv,
        _ => false,
    };

    // Every operand an instruction reads or writes, so no local can be missed.
    private static IEnumerable<Val> OperandsOf(Instruction instr)
    {
        switch (instr)
        {
            case Copy c: yield return c.Src; yield return c.Dst; break;
            case Bitcast b: yield return b.Src; yield return b.Dst; break;
            case Unary u: yield return u.Src; yield return u.Dst; break;
            case Binary bin: yield return bin.Src1; yield return bin.Src2; yield return bin.Dst; break;
            case AugAssign a: yield return a.Target; yield return a.Operand; break;
            case Return r: yield return r.Value; break;
            case LoadIndirect li: yield return li.SrcPtr; yield return li.Dst; break;
            case StoreIndirect si: yield return si.Src; yield return si.DstPtr; break;
            case JumpIfZero jz: yield return jz.Condition; break;
            case JumpIfNotZero jnz: yield return jnz.Condition; break;
            case JumpIfEqual j: yield return j.Src1; yield return j.Src2; break;
            case JumpIfNotEqual j: yield return j.Src1; yield return j.Src2; break;
            case JumpIfLessThan j: yield return j.Src1; yield return j.Src2; break;
            case JumpIfLessOrEqual j: yield return j.Src1; yield return j.Src2; break;
            case JumpIfGreaterThan j: yield return j.Src1; yield return j.Src2; break;
            case JumpIfGreaterOrEqual j: yield return j.Src1; yield return j.Src2; break;
            case JumpIfBitSet jbs: yield return jbs.Source; break;
            case JumpIfBitClear jbc: yield return jbc.Source; break;
            case BitSet bs: yield return bs.Target; break;
            case BitClear bc: yield return bc.Target; break;
            case BitCheck bck: yield return bck.Source; yield return bck.Dst; break;
            case BitWrite bw: yield return bw.Target; yield return bw.Src; break;
            case Call call:
                foreach (var a in call.Args) yield return a;
                yield return call.Dst;
                break;
            case IndirectCall ic:
                yield return ic.FuncAddr;
                foreach (var a in ic.Args) yield return a;
                yield return ic.Dst;
                break;
            case ArrayLoad al: yield return al.Index; yield return al.Dst; break;
            case ArrayStore ast: yield return ast.Index; yield return ast.Src; break;
            case ArrayLoadFlash alf: yield return alf.Index; yield return alf.Dst; break;
            case FlashLoadPtr flp: yield return flp.Ptr; yield return flp.Index; yield return flp.Dst; break;
            case BytearrayLoad bal: yield return bal.Index; yield return bal.Dst; break;
            case BytearrayStore bas: yield return bas.Index; yield return bas.Src; break;
            case SignalError se: yield return se.Code; break;
            case InlineAsm asm when asm.Operands is not null:
                foreach (var o in asm.Operands) yield return o;
                break;
        }
    }

    private void AllocateMissingSlots(Function func, ref int frameSize)
    {
        int next = stackLayout.Count > 0 ? stackLayout.Values.Min() : -8;

        foreach (var instr in func.Body)
        foreach (var val in OperandsOf(instr))
        {
            string name = ValName(val);
            if (string.IsNullOrEmpty(name)) continue;
            if (globalNames.Contains(name) || stackLayout.ContainsKey(name)) continue;

            next -= 4;
            stackLayout[name] = next;
        }

        int needed = -next;
        if (needed % 16 != 0) needed += 16 - (needed % 16);
        if (needed > frameSize) frameSize = needed;
    }

    // sp adjustments beyond the 12-bit immediate range need a scratch register.
    private void EmitStackAdjust(int delta)
    {
        if (delta >= -2048 && delta <= 2047)
        {
            Emit("addi", "sp", "sp", delta.ToString());
            return;
        }

        Emit("li", "t0", delta.ToString());
        Emit("add", "sp", "sp", "t0");
    }

    private void EmitFramePointer()
    {
        if (currentStackAdjustment <= 2047)
        {
            Emit("addi", "s0", "sp", currentStackAdjustment.ToString());
            return;
        }

        Emit("li", "s0", currentStackAdjustment.ToString());
        Emit("add", "s0", "s0", "sp");
    }

    private void CompileInstruction(Instruction instr)
    {
        switch (instr)
        {
            case Copy arg: CompileCopy(arg); break;
            case Return arg: CompileReturn(arg); break;
            case Jump arg: Emit("j", arg.Target); break;
            case JumpIfZero arg: LoadIntoReg(arg.Condition, "t0"); Emit("beqz", "t0", arg.Target); break;
            case JumpIfNotZero arg: LoadIntoReg(arg.Condition, "t0"); Emit("bnez", "t0", arg.Target); break;
            case JumpIfEqual arg: LoadIntoReg(arg.Src1, "t0"); LoadIntoReg(arg.Src2, "t1"); Emit("beq", "t0", "t1", arg.Target); break;
            case JumpIfNotEqual arg: LoadIntoReg(arg.Src1, "t0"); LoadIntoReg(arg.Src2, "t1"); Emit("bne", "t0", "t1", arg.Target); break;
            case JumpIfLessThan arg: LoadIntoReg(arg.Src1, "t0"); LoadIntoReg(arg.Src2, "t1"); Emit("blt", "t0", "t1", arg.Target); break;
            case JumpIfLessOrEqual arg: LoadIntoReg(arg.Src1, "t0"); LoadIntoReg(arg.Src2, "t1"); Emit("ble", "t0", "t1", arg.Target); break;
            case JumpIfGreaterThan arg: LoadIntoReg(arg.Src1, "t0"); LoadIntoReg(arg.Src2, "t1"); Emit("bgt", "t0", "t1", arg.Target); break;
            case JumpIfGreaterOrEqual arg: LoadIntoReg(arg.Src1, "t0"); LoadIntoReg(arg.Src2, "t1"); Emit("bge", "t0", "t1", arg.Target); break;
            case Label arg: EmitLabel(arg.Name); break;
            case Call arg: CompileCall(arg); break;
            case IndirectCall arg: CompileIndirectCall(arg); break;
            case Bitcast arg: CompileCopy(new Copy(arg.Src, arg.Dst)); break;
            case SignalSuccess: Emit("li", ErrorReg, "0"); break;
            case BranchOnError arg: Emit("bnez", ErrorReg, arg.ErrorLabel); break;
            case SignalError arg: CompileSignalError(arg); break;
            case Unary arg: CompileUnary(arg); break;
            case Binary arg: CompileBinary(arg); break;
            case BitSet arg: CompileBitSet(arg); break;
            case BitClear arg: CompileBitClear(arg); break;
            case BitCheck arg: CompileBitCheck(arg); break;
            case BitWrite arg: CompileBitWrite(arg); break;
            case JumpIfBitSet arg: LoadIntoReg(arg.Source, "t0"); Emit("srli", "t0", "t0", arg.Bit.ToString()); Emit("andi", "t0", "t0", "1"); Emit("bnez", "t0", arg.Target); break;
            case JumpIfBitClear arg: LoadIntoReg(arg.Source, "t0"); Emit("srli", "t0", "t0", arg.Bit.ToString()); Emit("andi", "t0", "t0", "1"); Emit("beqz", "t0", arg.Target); break;
            case AugAssign aa: CompileBinary(new Binary(aa.Op, aa.Target, aa.Operand, aa.Target)); break;
            case LoadIndirect li:
                LoadIntoReg(li.SrcPtr, "t0");
                Emit(LoadMnemonic(li.Elem), "t1", "0(t0)");
                StoreRegInto("t1", li.Dst);
                break;
            case StoreIndirect si:
                LoadIntoReg(si.DstPtr, "t0");
                LoadIntoReg(si.Src, "t1");
                Emit(StoreMnemonic(si.Elem), "t1", "0(t0)");
                break;
            case ArrayLoad arg: CompileArrayLoad(arg); break;
            case ArrayStore arg: CompileArrayStore(arg); break;
            case ArrayLoadFlash arg: CompileArrayLoadFlash(arg); break;
            case FlashLoadPtr arg: CompileFlashLoadPtr(arg); break;
            case BytearrayLoad arg: CompileBytearrayLoad(arg); break;
            case BytearrayStore arg: CompileBytearrayStore(arg); break;
            // Emitted with the other read-only data once every function is done.
            case FlashData arg: flashTables[arg.Name] = arg.Bytes; break;
            // Outlining boundaries: AVR uses them to fold repeated expansions
            // into one subroutine. This backend emits expansions in place, so
            // the markers carry no code.
            case InlineExpansionMarker: break;
            case InlineAsm arg: CompileInlineAsm(arg); break;
            case DebugLine arg:
                if (!string.IsNullOrEmpty(arg.SourceFile)) EmitComment($"{arg.SourceFile}:{arg.Line}: {arg.Text}");
                else EmitComment($"Line {arg.Line}: {arg.Text}");
                break;
            default:
                throw new NotSupportedException(
                    $"RISC-V backend: IR instruction '{instr.GetType().Name}' is not supported yet. " +
                    "This target currently covers integer arithmetic, control flow, bit operations, " +
                    "direct/indirect calls and MMIO access.");
        }
    }

    // ─── Indexed access ──────────────────────────────────────────────────────

    // Advances t0 (an element base address) by Index scaled to the element size.
    // Runtime indices go through t1; t2 stays free because LoadIntoReg uses it.
    private void EmitScaledIndexAdd(Val index, DataType elem)
    {
        int size = elem.SizeOf();

        if (index is Constant c)
        {
            int offset = c.Value * size;
            if (offset == 0) return;
            if (offset >= -2048 && offset <= 2047)
                Emit("addi", "t0", "t0", offset.ToString());
            else
            {
                Emit("li", "t1", offset.ToString());
                Emit("add", "t0", "t0", "t1");
            }

            return;
        }

        LoadIntoReg(index, "t1");
        int shift = size switch { 2 => 1, 4 => 2, 8 => 3, _ => 0 };
        if (shift > 0) Emit("slli", "t1", "t1", shift.ToString());
        Emit("add", "t0", "t0", "t1");
    }

    private void CompileArrayLoad(ArrayLoad arg)
    {
        NoteArray(arg.ArrayName, arg.Count, arg.ElemType);
        Emit("la", "t0", arg.ArrayName);
        EmitScaledIndexAdd(arg.Index, arg.ElemType);
        Emit(LoadMnemonic(arg.ElemType), "t1", "0(t0)");
        StoreRegInto("t1", arg.Dst);
    }

    private void CompileArrayStore(ArrayStore arg)
    {
        NoteArray(arg.ArrayName, arg.Count, arg.ElemType);
        Emit("la", "t0", arg.ArrayName);
        EmitScaledIndexAdd(arg.Index, arg.ElemType);
        LoadIntoReg(arg.Src, "t1");
        Emit(StoreMnemonic(arg.ElemType), "t1", "0(t0)");
    }

    // Flash is directly addressable here, so a PROGMEM read is an ordinary load
    // from .rodata — no LPM dance like AVR needs.
    private void CompileArrayLoadFlash(ArrayLoadFlash arg)
    {
        Emit("la", "t0", FlashSymbol(arg.ArrayName));
        EmitScaledIndexAdd(arg.Index, DataType.UINT8);
        Emit("lbu", "t1", "0(t0)");
        StoreRegInto("t1", arg.Dst);
    }

    private void CompileFlashLoadPtr(FlashLoadPtr arg)
    {
        LoadIntoReg(arg.Ptr, "t0");
        EmitScaledIndexAdd(arg.Index, DataType.UINT8);
        Emit("lbu", "t1", "0(t0)");
        StoreRegInto("t1", arg.Dst);
    }

    private void CompileBytearrayLoad(BytearrayLoad arg)
    {
        LoadIntoReg(new Variable(arg.PtrName), "t0");
        EmitScaledIndexAdd(arg.Index, DataType.UINT8);
        Emit("lbu", "t1", "0(t0)");
        StoreRegInto("t1", arg.Dst);
    }

    private void CompileBytearrayStore(BytearrayStore arg)
    {
        LoadIntoReg(new Variable(arg.PtrName), "t0");
        EmitScaledIndexAdd(arg.Index, DataType.UINT8);
        LoadIntoReg(arg.Src, "t1");
        Emit("sb", "t1", "0(t0)");
    }

    // Registers handed to asm() operands. They are argument registers, so any
    // value the codegen keeps live across the block is already in memory.
    private static readonly string[] AsmOperandRegs = ["a0", "a1", "a2", "a3"];

    private void CompileInlineAsm(InlineAsm arg)
    {
        if (arg.Operands is null || arg.Operands.Count == 0)
        {
            assembly.Add(RISCVAsmLine.MakeRaw(arg.Code));
            return;
        }

        if (arg.Operands.Count > AsmOperandRegs.Length)
            throw new NotSupportedException(
                $"RISC-V backend: asm() takes at most {AsmOperandRegs.Length} operands " +
                $"(%0-%{AsmOperandRegs.Length - 1}); this block declares {arg.Operands.Count}.");

        // Operands are read-write: loaded in, substituted, written back out.
        for (int i = 0; i < arg.Operands.Count; i++)
            LoadIntoReg(arg.Operands[i], AsmOperandRegs[i]);

        string code = arg.Code;
        for (int i = 0; i < arg.Operands.Count; i++)
            code = code.Replace($"%{i}", AsmOperandRegs[i]);

        assembly.Add(RISCVAsmLine.MakeRaw(code));

        for (int i = 0; i < arg.Operands.Count; i++)
            StoreRegInto(AsmOperandRegs[i], arg.Operands[i]);
    }

    private void CompileCall(Call arg)
    {
        LoadCallArguments(arg.Args, arg.FunctionName);
        Emit("call", arg.FunctionName);
        StoreRegInto("a0", arg.Dst);
    }

    private void CompileIndirectCall(IndirectCall arg)
    {
        // The target address is materialised first: LoadIntoReg only ever clobbers
        // its destination and t2, so it survives the argument setup below.
        LoadIntoReg(arg.FuncAddr, "t0");
        LoadCallArguments(arg.Args, "an indirect call");
        Emit("jalr", "t0");
        StoreRegInto("a0", arg.Dst);
    }

    private void LoadCallArguments(List<Val> args, string callee)
    {
        if (args.Count > ArgRegs.Length)
            throw new NotSupportedException(
                $"RISC-V backend: {callee} passes {args.Count} arguments; the ilp32e ABI " +
                $"passes at most {ArgRegs.Length} in registers and stack-passed arguments " +
                "are not implemented yet.");

        for (int i = 0; i < args.Count; i++)
            LoadIntoReg(args[i], ArgRegs[i]);
    }

    private void CompileReturn(Return arg)
    {
        if (arg.Value is not NoneVal)
            LoadIntoReg(arg.Value, "a0");

        EmitEpilogue();
    }

    // Raising an error leaves the function the same way a return does, so the
    // teardown is shared. main has nowhere to return to and parks instead.
    private void CompileSignalError(SignalError arg)
    {
        LoadIntoReg(arg.Code, ErrorReg);

        if (arg.CatchLabel is not null)
            Emit("j", arg.CatchLabel);
        else
            EmitEpilogue();
    }

    private void EmitEpilogue()
    {
        if (currentFuncName == "main")
        {
            string endLabel = MakeLabel("end_loop");
            EmitLabel(endLabel);
            Emit("j", endLabel);
            return;
        }

        if (!currentIsLeaf)
            Emit("lw", "ra", $"{currentStackAdjustment - 4}(sp)");
        Emit("lw", "s0", $"{currentStackAdjustment - 8}(sp)");
        EmitStackAdjust(currentStackAdjustment);

        if (currentIsInterrupt)
        {
            EmitContextRestore();
            EmitInterruptReturn();
            return;
        }

        Emit("ret");
    }

    private void CompileCopy(Copy arg)
    {
        LoadIntoReg(arg.Src, "t0");
        StoreRegInto("t0", arg.Dst);
    }

    private void CompileUnary(Unary arg)
    {
        LoadIntoReg(arg.Src, "t0");
        switch (arg.Op)
        {
            case PyMCU.IR.UnaryOp.Neg: Emit("neg", "t0", "t0"); break;
            case PyMCU.IR.UnaryOp.BitNot: Emit("not", "t0", "t0"); break;
            case PyMCU.IR.UnaryOp.Not: Emit("seqz", "t0", "t0"); break;
        }
        StoreRegInto("t0", arg.Dst);
    }

    private void CompileBinary(Binary arg)
    {
        LoadIntoReg(arg.Src1, "t0");

        bool usedImmediate = false;
        if (arg.Src2 is Constant c2 && c2.Value >= -2048 && c2.Value <= 2047)
        {
            int val = c2.Value;
            switch (arg.Op)
            {
                case PyMCU.IR.BinaryOp.Add: Emit("addi", "t0", "t0", val.ToString()); usedImmediate = true; break;
                case PyMCU.IR.BinaryOp.Sub: Emit("addi", "t0", "t0", (-val).ToString()); usedImmediate = true; break;
                case PyMCU.IR.BinaryOp.BitAnd: Emit("andi", "t0", "t0", val.ToString()); usedImmediate = true; break;
                case PyMCU.IR.BinaryOp.BitOr: Emit("ori", "t0", "t0", val.ToString()); usedImmediate = true; break;
                case PyMCU.IR.BinaryOp.BitXor: Emit("xori", "t0", "t0", val.ToString()); usedImmediate = true; break;
                case PyMCU.IR.BinaryOp.LessThan: Emit("slti", "t0", "t0", val.ToString()); usedImmediate = true; break;
            }
        }

        if (!usedImmediate)
        {
            LoadIntoReg(arg.Src2, "t1");
            switch (arg.Op)
            {
                case PyMCU.IR.BinaryOp.Add: Emit("add", "t0", "t0", "t1"); break;
                case PyMCU.IR.BinaryOp.Sub: Emit("sub", "t0", "t0", "t1"); break;
                case PyMCU.IR.BinaryOp.BitAnd: Emit("and", "t0", "t0", "t1"); break;
                case PyMCU.IR.BinaryOp.BitOr: Emit("or", "t0", "t0", "t1"); break;
                case PyMCU.IR.BinaryOp.BitXor: Emit("xor", "t0", "t0", "t1"); break;
                // Cores with the M extension multiply in one instruction; the
                // rest pay for a software helper.
                case PyMCU.IR.BinaryOp.Mul when Profile.HasMulDiv:
                    Emit("mul", "t0", "t0", "t1");
                    break;
                case PyMCU.IR.BinaryOp.Mul:
                    Emit("mv", "a0", "t0"); Emit("mv", "a1", "t1");
                    Emit("call", "__mulsi3"); Emit("mv", "t0", "a0");
                    break;

                // `/` and `//` are the same operation on integers here, and both
                // follow Python: the quotient floors toward negative infinity.
                // Unsigned operands can go straight to the hardware (or the
                // unsigned helper) because flooring and truncation agree when
                // nothing is negative.
                case PyMCU.IR.BinaryOp.Div:
                case PyMCU.IR.BinaryOp.FloorDiv:
                    EmitDivision(arg, wantRemainder: false);
                    break;
                case PyMCU.IR.BinaryOp.Mod:
                    EmitDivision(arg, wantRemainder: true);
                    break;
                case PyMCU.IR.BinaryOp.Equal: Emit("xor", "t0", "t0", "t1"); Emit("seqz", "t0", "t0"); break;
                case PyMCU.IR.BinaryOp.NotEqual: Emit("xor", "t0", "t0", "t1"); Emit("snez", "t0", "t0"); break;
                case PyMCU.IR.BinaryOp.LessThan: Emit("slt", "t0", "t0", "t1"); break;
                case PyMCU.IR.BinaryOp.GreaterEqual: Emit("slt", "t0", "t0", "t1"); Emit("seqz", "t0", "t0"); break;
                case PyMCU.IR.BinaryOp.GreaterThan: Emit("slt", "t0", "t1", "t0"); break;
                case PyMCU.IR.BinaryOp.LessEqual: Emit("slt", "t0", "t1", "t0"); Emit("seqz", "t0", "t0"); break;
                case PyMCU.IR.BinaryOp.LShift: Emit("sll", "t0", "t0", "t1"); break;
                case PyMCU.IR.BinaryOp.RShift: Emit("srl", "t0", "t0", "t1"); break;
            }
        }

        StoreRegInto("t0", arg.Dst);
    }

    // Lowers Div/FloorDiv/Mod with the operands already in t0 and t1, leaving the
    // result in t0. Which routine applies depends on signedness and on whether
    // the core divides in hardware.
    private void EmitDivision(Binary arg, bool wantRemainder)
    {
        bool signed = IsSignedContext(arg.Src1, arg.Src2);

        if (!signed && Profile.HasMulDiv)
        {
            Emit(wantRemainder ? "remu" : "divu", "t0", "t0", "t1");
            return;
        }

        string helper = (signed, wantRemainder) switch
        {
            (true, false) => "__floordivsi3",
            (true, true) => "__floormodsi3",
            (false, false) => "__udivsi3",
            (false, true) => "__umodsi3",
        };

        if (signed) needsFloorDivMod = true;
        else needsUDivMod = true;

        Emit("mv", "a0", "t0");
        Emit("mv", "a1", "t1");
        Emit("call", helper);
        Emit("mv", "t0", "a0");
    }

    private void CompileBitSet(BitSet arg)
    {
        LoadIntoReg(arg.Target, "t0");
        Emit("li", "t1", (1 << arg.Bit).ToString());
        Emit("or", "t0", "t0", "t1");
        StoreRegInto("t0", arg.Target);
    }

    private void CompileBitClear(BitClear arg)
    {
        LoadIntoReg(arg.Target, "t0");
        Emit("li", "t1", (~(1 << arg.Bit)).ToString());
        Emit("and", "t0", "t0", "t1");
        StoreRegInto("t0", arg.Target);
    }

    private void CompileBitCheck(BitCheck arg)
    {
        LoadIntoReg(arg.Source, "t0");
        Emit("srli", "t0", "t0", arg.Bit.ToString());
        Emit("andi", "t0", "t0", "1");
        StoreRegInto("t0", arg.Dst);
    }

    private void CompileBitWrite(BitWrite arg)
    {
        LoadIntoReg(arg.Src, "t0");
        LoadIntoReg(arg.Target, "t1");
        Emit("li", "t2", (~(1 << arg.Bit)).ToString());
        Emit("and", "t1", "t1", "t2");
        Emit("snez", "t0", "t0");
        Emit("slli", "t0", "t0", arg.Bit.ToString());
        Emit("or", "t1", "t1", "t0");
        StoreRegInto("t1", arg.Target);
    }
}
