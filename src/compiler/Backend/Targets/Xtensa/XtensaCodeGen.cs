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

// Xtensa code generation backend for PyMCU.
//
// Targets: ESP8266 (LX106), ESP32 (LX6), ESP32-S2 (LX7), ESP32-S3 (LX7).
// ABI:     call0 (flat register model — no windowed register rotation).
//
// Register conventions (call0 ABI):
//   a0  — return address (set by call0 instruction)
//   a1  — stack pointer (grows downward)
//   a2  — first argument / return value
//   a3-a7 — arguments 2-6
//   a8  — scratch temp 0  (caller-saved, used as t0)
//   a9  — scratch temp 1  (caller-saved, used as t1)
//   a10 — scratch temp 2  (caller-saved, used as t2)
//   a12 — callee-saved frame pointer (equivalent to RISC-V s0)
//   a13-a15 — callee-saved (not used by this codegen)
//
// Stack frame layout (frame size = N, 16-byte aligned):
//   SP+N-4:  saved a0 (return address)   — non-leaf functions only
//   SP+N-8:  saved a12 (frame pointer)
//   SP+N-12: first local variable
//   SP+N-16: second local variable
//   ...
//
// Literal pool:
//   32-bit constants that do not fit in movi's 12-bit signed range are
//   placed in the literal pool using the .literal directive.  The assembler
//   emits them at the nearest preceding .literal_position marker.  Because
//   l32r uses a PC-relative backward reference, the .literal_position is
//   emitted before each function so the literal pool always precedes the
//   l32r instructions that reference it.

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
    private List<(string Label, long Value)> pendingLiterals = [];

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
    // inline.  Larger values (including peripheral addresses) are placed in the
    // per-function literal pool and loaded with l32r.
    private void LoadImmediate(string reg, long value)
    {
        if (value >= -2048 && value <= 2047)
        {
            Emit("movi", reg, value.ToString());
        }
        else
        {
            string litLabel = $".Llit_{currentFuncName}_{litCounter++}";
            pendingLiterals.Add((litLabel, value));
            Emit("l32r", reg, litLabel);
        }
    }

    // -------------------------------------------------------------------------
    // Stack offset translation
    // -------------------------------------------------------------------------

    // The DynamicStackAllocator returns FP-relative offsets (negative).
    // Convert to SP-relative (positive) for l32i/s32i.
    private int SpOffset(int fpOffset) => currentStackAdjustment + fpOffset;

    // -------------------------------------------------------------------------
    // Operand load / store
    // -------------------------------------------------------------------------

    private void LoadIntoReg(Val val, string reg)
    {
        switch (val)
        {
            case Constant c:
                LoadImmediate(reg, c.Value);
                break;

            case MemoryAddress mem:
                LoadImmediate("a10", (long)mem.Address);
                Emit("l32i", reg, "a10", "0");
                break;

            default:
            {
                string name = val is Variable vr ? vr.Name : val is Temporary tr ? tr.Name : "";
                if (!string.IsNullOrEmpty(name) && stackLayout.TryGetValue(name, out int offset))
                {
                    int spOff = SpOffset(offset);
                    Emit("l32i", reg, "a1", spOff.ToString());
                }
                else if (!string.IsNullOrEmpty(name))
                {
                    EmitRaw($"\t.literal .Lgbl_{name}_{litCounter}, {name}");
                    Emit("l32r", "a10", $".Lgbl_{name}_{litCounter++}");
                    Emit("l32i", reg, "a10", "0");
                }
                break;
            }
        }
    }

    private void StoreRegInto(string reg, Val val)
    {
        switch (val)
        {
            case Constant:
                return;

            case MemoryAddress mem:
                LoadImmediate("a10", (long)mem.Address);
                Emit("s32i", reg, "a10", "0");
                break;

            default:
            {
                string name = val is Variable vr ? vr.Name : val is Temporary tr ? tr.Name : "";
                if (!string.IsNullOrEmpty(name) && stackLayout.TryGetValue(name, out int offset))
                {
                    int spOff = SpOffset(offset);
                    Emit("s32i", reg, "a1", spOff.ToString());
                }
                else if (!string.IsNullOrEmpty(name))
                {
                    EmitRaw($"\t.literal .Lgbl_{name}_{litCounter}, {name}");
                    Emit("l32r", "a10", $".Lgbl_{name}_{litCounter++}");
                    Emit("s32i", reg, "a10", "0");
                }
                break;
            }
        }
    }

    // -------------------------------------------------------------------------
    // Top-level compilation
    // -------------------------------------------------------------------------

    public override void EmitContextSave() { }
    public override void EmitContextRestore() { }
    public override void EmitInterruptReturn() { }

    public override void Compile(ProgramIR program, TextWriter output)
    {
        assembly.Clear();
        EmitComment($"Generated by pymcuc for Xtensa ({cfg.Chip})");
        EmitRaw(".option call0");
        EmitRaw(".section .text");
        EmitRaw(".align 4");

        foreach (var func in program.Functions)
            CompileFunction(func);

        var optimized = XtensaPeephole.Optimize(assembly);
        foreach (var line in optimized)
            output.WriteLine(line.ToString());
    }

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
        // literal pool (which is populated during body compilation).
        var savedAssembly = assembly;
        assembly = [];

        EmitRaw($".global {func.Name}");
        EmitRaw($".type {func.Name}, @function");
        EmitRaw(".align 4");
        EmitLabel(func.Name);

        // For the bare-metal entry point, initialise the stack pointer to the
        // top of DRAM before the standard prologue adjusts it.  The addresses
        // below are the inclusive top of each chip's DRAM region:
        //   ESP8266 / LX106  — DRAM ends at 0x40000000 (96 KB)
        //   ESP32-S3          — DRAM ends at 0x3FCFFFFF (512 KB)
        //   ESP32-S2          — DRAM ends at 0x3FFFFFFF (320 KB)
        //   ESP32 / generic   — DRAM ends at 0x3FFFFFFF (320 KB)
        // Reference: ESP-IDF memory layout docs, Xtensa LX6/LX7 TRM.
        if (func.Name == "main")
        {
            string stackTop = cfg.Chip.ToLowerInvariant() switch
            {
                var c when c.StartsWith("esp8266") || c == "lx106" => "0x40000000",
                var c when c.StartsWith("esp32s3") => "0x3FCFFFFF",
                var c when c.StartsWith("esp32s2") => "0x3FFFFFFF",
                _ => "0x3FFFFFFF"   // esp32 / generic Xtensa
            };
            EmitComment("initialise stack pointer to top of DRAM");
            Emit("movi", "a1", stackTop);
        }

        // Prologue
        Emit("addi", "a1", "a1", (-currentStackAdjustment).ToString());
        if (!currentIsLeaf)
            Emit("s32i", "a0", "a1", (currentStackAdjustment - 4).ToString());
        Emit("s32i", "a12", "a1", (currentStackAdjustment - 8).ToString());
        Emit("addi", "a12", "a1", currentStackAdjustment.ToString());

        foreach (var instr in func.Body)
            CompileInstruction(instr);

        var funcLines = assembly;
        assembly = savedAssembly;

        // Emit .literal_position and collected literals before the function body
        if (pendingLiterals.Count > 0)
        {
            EmitRaw(".literal_position");
            foreach (var (label, value) in pendingLiterals)
                EmitRaw($".literal {label}, {(int)value}");
        }

        foreach (var line in funcLines)
            assembly.Add(line);

        EmitRaw($".size {func.Name}, . - {func.Name}");
    }

    // -------------------------------------------------------------------------
    // Instruction dispatch
    // -------------------------------------------------------------------------

    private void CompileInstruction(Instruction instr)
    {
        switch (instr)
        {
            case Copy arg: CompileCopy(arg); break;
            case Return arg: CompileReturn(arg); break;
            case Jump arg: Emit("j", arg.Target); break;
            case JumpIfZero arg: LoadIntoReg(arg.Condition, "a8"); Emit("beqz", "a8", arg.Target); break;
            case JumpIfNotZero arg: LoadIntoReg(arg.Condition, "a8"); Emit("bnez", "a8", arg.Target); break;
            case JumpIfEqual arg:
                LoadIntoReg(arg.Src1, "a8"); LoadIntoReg(arg.Src2, "a9");
                Emit("beq", "a8", "a9", arg.Target); break;
            case JumpIfNotEqual arg:
                LoadIntoReg(arg.Src1, "a8"); LoadIntoReg(arg.Src2, "a9");
                Emit("bne", "a8", "a9", arg.Target); break;
            case JumpIfLessThan arg:
                LoadIntoReg(arg.Src1, "a8"); LoadIntoReg(arg.Src2, "a9");
                Emit("blt", "a8", "a9", arg.Target); break;
            case JumpIfLessOrEqual arg:
                LoadIntoReg(arg.Src1, "a8"); LoadIntoReg(arg.Src2, "a9");
                Emit("bge", "a9", "a8", arg.Target); break;
            case JumpIfGreaterThan arg:
                LoadIntoReg(arg.Src1, "a8"); LoadIntoReg(arg.Src2, "a9");
                Emit("blt", "a9", "a8", arg.Target); break;
            case JumpIfGreaterOrEqual arg:
                LoadIntoReg(arg.Src1, "a8"); LoadIntoReg(arg.Src2, "a9");
                Emit("bge", "a8", "a9", arg.Target); break;
            case JumpIfBitSet arg:
                LoadIntoReg(arg.Source, "a8");
                Emit("bbsi", "a8", arg.Bit.ToString(), arg.Target); break;
            case JumpIfBitClear arg:
                LoadIntoReg(arg.Source, "a8");
                Emit("bbci", "a8", arg.Bit.ToString(), arg.Target); break;
            case Label arg: EmitLabel(arg.Name); break;
            case Call arg: CompileCall(arg); break;
            case Unary arg: CompileUnary(arg); break;
            case Binary arg: CompileBinary(arg); break;
            case BitSet arg: CompileBitSet(arg); break;
            case BitClear arg: CompileBitClear(arg); break;
            case BitCheck arg: CompileBitCheck(arg); break;
            case BitWrite arg: CompileBitWrite(arg); break;
            case AugAssign: throw new NotSupportedException("Xtensa: AugAssign is not yet implemented");
            case LoadIndirect: throw new NotSupportedException("Xtensa: LoadIndirect is not yet implemented");
            case StoreIndirect: throw new NotSupportedException("Xtensa: StoreIndirect is not yet implemented");
            case InlineAsm arg: assembly.Add(XtensaAsmLine.MakeRaw(arg.Code)); break;
            case DebugLine arg:
                if (!string.IsNullOrEmpty(arg.SourceFile))
                    EmitComment($"{arg.SourceFile}:{arg.Line}: {arg.Text}");
                else
                    EmitComment($"Line {arg.Line}: {arg.Text}");
                break;
            case ArrayLoad: break;
            case ArrayStore: break;
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
            // Bare-metal entry: spin forever
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
        // Pass arguments a2-a7 (call0 ABI)
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
            case PyMCU.IR.UnaryOp.Neg: Emit("neg", "a8", "a8"); break;
            case PyMCU.IR.UnaryOp.BitNot: Emit("bnot", "a8", "a8"); break;
            case PyMCU.IR.UnaryOp.Not:
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
        StoreRegInto("a8", arg.Dst);
    }

    // -------------------------------------------------------------------------
    // Binary
    // -------------------------------------------------------------------------

    private void CompileBinary(Binary arg)
    {
        LoadIntoReg(arg.Src1, "a8");

        // Try immediate-form instructions for small constant RHS
        bool usedImmediate = false;
        if (arg.Src2 is Constant c2 && c2.Value >= -2048 && c2.Value <= 2047)
        {
            int val = c2.Value;
            switch (arg.Op)
            {
                case PyMCU.IR.BinaryOp.Add:
                    Emit("addi", "a8", "a8", val.ToString());
                    usedImmediate = true;
                    break;
                case PyMCU.IR.BinaryOp.Sub:
                    Emit("addi", "a8", "a8", (-val).ToString());
                    usedImmediate = true;
                    break;
            }
        }

        if (!usedImmediate)
        {
            LoadIntoReg(arg.Src2, "a9");
            switch (arg.Op)
            {
                case PyMCU.IR.BinaryOp.Add: Emit("add", "a8", "a8", "a9"); break;
                case PyMCU.IR.BinaryOp.Sub: Emit("sub", "a8", "a8", "a9"); break;
                case PyMCU.IR.BinaryOp.BitAnd: Emit("and", "a8", "a8", "a9"); break;
                case PyMCU.IR.BinaryOp.BitOr: Emit("or", "a8", "a8", "a9"); break;
                case PyMCU.IR.BinaryOp.BitXor: Emit("xor", "a8", "a8", "a9"); break;
                case PyMCU.IR.BinaryOp.LShift: Emit("ssl", "a9"); Emit("sll", "a8", "a8"); break;
                case PyMCU.IR.BinaryOp.RShift: Emit("ssr", "a9"); Emit("srl", "a8", "a8"); break;
                case PyMCU.IR.BinaryOp.Mul:
                    Emit("mov", "a2", "a8"); Emit("mov", "a3", "a9");
                    Emit("call0", "__mulsi3"); Emit("mov", "a8", "a2");
                    break;
                case PyMCU.IR.BinaryOp.Div:
                case PyMCU.IR.BinaryOp.FloorDiv:
                    Emit("mov", "a2", "a8"); Emit("mov", "a3", "a9");
                    Emit("call0", "__divsi3"); Emit("mov", "a8", "a2");
                    break;
                case PyMCU.IR.BinaryOp.Mod:
                    Emit("mov", "a2", "a8"); Emit("mov", "a3", "a9");
                    Emit("call0", "__modsi3"); Emit("mov", "a8", "a2");
                    break;
                case PyMCU.IR.BinaryOp.Equal:
                    Emit("xor", "a8", "a8", "a9");
                    CompileSeqz("a8");
                    break;
                case PyMCU.IR.BinaryOp.NotEqual:
                    Emit("xor", "a8", "a8", "a9");
                    CompileSnez("a8");
                    break;
                case PyMCU.IR.BinaryOp.LessThan:
                    CompileSlt("a8", "a8", "a9");
                    break;
                case PyMCU.IR.BinaryOp.GreaterEqual:
                    CompileSlt("a8", "a8", "a9");
                    CompileSeqz("a8");
                    break;
                case PyMCU.IR.BinaryOp.GreaterThan:
                    CompileSlt("a8", "a9", "a8");
                    break;
                case PyMCU.IR.BinaryOp.LessEqual:
                    CompileSlt("a8", "a9", "a8");
                    CompileSeqz("a8");
                    break;
            }
        }

        StoreRegInto("a8", arg.Dst);
    }

    // Xtensa has no seqz/snez/slt pseudo-instructions; emulate them with branches.
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
        // extui: extract bit field (4 operands: dst, src, low-bit, bitcount)
        Emit("extui", "a8", "a8", arg.Bit.ToString(), "1");
        StoreRegInto("a8", arg.Dst);
    }

    private void CompileBitWrite(BitWrite arg)
    {
        LoadIntoReg(arg.Src, "a8");
        LoadIntoReg(arg.Target, "a9");
        // Clear the target bit
        Emit("movi", "a10", (~(1 << arg.Bit)).ToString());
        Emit("and", "a9", "a9", "a10");
        // Normalise src to 0 or 1
        CompileSnez("a8");
        // Shift src into position
        Emit("movi", "a10", arg.Bit.ToString());
        Emit("ssl", "a10");
        Emit("sll", "a8", "a8");
        // Merge
        Emit("or", "a9", "a9", "a8");
        StoreRegInto("a9", arg.Target);
    }
}
