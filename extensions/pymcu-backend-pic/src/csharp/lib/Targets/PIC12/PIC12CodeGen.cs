/*
 * -----------------------------------------------------------------------------
 * PyMCU Compiler (pymcuc) — PIC12 (Baseline) Backend  [WIP]
 * Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
 *
 * SPDX-License-Identifier: MIT
 *
 * -----------------------------------------------------------------------------
 * STATUS: Work In Progress.  No cycle-accurate PIC12 simulator is integrated
 * into the test suite.  Use with caution.
 * -----------------------------------------------------------------------------
 *
 * Architecture notes (PIC12 / PIC10F / PIC12F baseline):
 *   - 12-bit instruction words.
 *   - W register is the sole accumulator.
 *   - STATUS register at 0x03: C=bit0, DC=bit1, Z=bit2.
 *   - Conditional branches: BTFSS/BTFSC STATUS, bit + GOTO.
 *   - Very limited instruction set (~33 instructions).
 *   - Only 2-level hardware call stack (CALL depth = 2).
 *   - Data memory extremely limited (e.g., 25 bytes GPR on PIC12F629).
 *   - No SUBLW; subtraction via SUBWF only.
 *   - No hardware multiply.
 *   - Single interrupt at 0x0004 (if present — PIC10F has no interrupt).
 *
 * Memory layout:
 *   0x07-0x1D  User variables (PIC12F: GPR starts at 0x20, but 0x07 on PIC10F).
 *              VarBase = 0x20 for PIC12F (16F-compatible layout).
 *   0x28       TMP_LO
 *   0x29       TMP_HI
 *   0x2A       TMP2_LO
 *   0x2B       TMP2_HI
 *   0x2C       RETVAL_HI
 *   0x2D-0x2F  ARG_BASE (3 bytes; PIC12 programs typically have no deep call chains)
 *
 * Known WIP limitations:
 *   - Only 8-bit operations are meaningfully supported.
 *   - 16-bit operations are stubbed (emit a comment).
 *   - No banking support.
 *   - Flash/ROM tables not implemented.
 *   - SUBLW is absent on true PIC12 baseline (only available on enhanced-baseline).
 *     Subtraction uses SUBWF exclusively — requires one operand in a register.
 *   - Only 2-level hardware stack; deeply nested calls will overflow silently.
 * -----------------------------------------------------------------------------
 */

using PyMCU.Backend.Analysis;
using PyMCU.Backend.Targets.PIC;
using PyMCU.Common.Models;
using PyMCU.IR;
using IrBinOp = PyMCU.IR.BinaryOp;
using IrUnOp  = PyMCU.IR.UnaryOp;

namespace PyMCU.Backend.Targets.PIC12;

public class PIC12CodeGen(DeviceConfig cfg) : CodeGen
{
    private readonly List<PicAsmLine> _assembly = [];
    private Dictionary<string, int>  _varAddrs  = new();
    private Dictionary<string, int>  _varSizes  = new();
    private int                      _labelCounter;

    private const int StatusReg = 0x03;
    private const int StatusC   = 0;
    private const int StatusZ   = 2;

    private const int TmpLo    = 0x28;
    private const int TmpHi    = 0x29;
    private const int Tmp2Lo   = 0x2A;
    private const int Tmp2Hi   = 0x2B;
    private const int RetValHi = 0x2C;
    private const int ArgBase  = 0x2D;
    private const int VarBase  = 0x20;

    // -------------------------------------------------------------------------
    // Emit helpers
    // -------------------------------------------------------------------------

    private string MakeLabel(string pfx = ".L") => $"{pfx}_{_labelCounter++}";

    private void Emit(string m) => _assembly.Add(PicAsmLine.MakeInstruction(m));
    private void Emit(string m, string o1) =>
        _assembly.Add(PicAsmLine.MakeInstruction($"{m}\t{o1}"));
    private void Emit(string m, string o1, string o2) =>
        _assembly.Add(PicAsmLine.MakeInstruction($"{m}\t{o1}, {o2}"));

    private void EmitLabel(string l)   => _assembly.Add(PicAsmLine.MakeLabel(l));
    private void EmitComment(string c) => _assembly.Add(PicAsmLine.MakeComment(c));
    private void EmitRaw(string t)     => _assembly.Add(PicAsmLine.MakeRaw(t));

    private static string Addr(int a) => $"0x{a:X2}";

    // -------------------------------------------------------------------------
    // Type / address utilities
    // -------------------------------------------------------------------------

    private static DataType GetValType(Val val) => val switch
    {
        Variable v  => v.Type,
        Temporary t => t.Type,
        _ => DataType.UINT8,
    };

    private int AddrOf(Val val)
    {
        var name = val switch { Variable v => v.Name, Temporary t => t.Name, _ => "" };
        return !string.IsNullOrEmpty(name) && _varAddrs.TryGetValue(name, out int a) ? a : -1;
    }

    // -------------------------------------------------------------------------
    // Load / Store primitives
    // -------------------------------------------------------------------------

    private void LoadIntoW(Val val)
    {
        switch (val)
        {
            case Constant c:
                Emit("MOVLW", $"0x{c.Value & 0xFF:X2}");
                return;
            case MemoryAddress mem:
                Emit("MOVF", Addr(mem.Address), "W");
                return;
        }
        int addr = AddrOf(val);
        if (addr >= 0)
            Emit("MOVF", Addr(addr), "W");
        else
            EmitComment($"TODO(wip): unresolved load for {val}");
    }

    private void StoreW(Val dst)
    {
        if (dst is Constant) return;
        if (dst is MemoryAddress mem) { Emit("MOVWF", Addr(mem.Address)); return; }
        int addr = AddrOf(dst);
        if (addr >= 0)
            Emit("MOVWF", Addr(addr));
        else
            EmitComment($"TODO(wip): unresolved store for {dst}");
    }

    // -------------------------------------------------------------------------
    // Compare: after EmitCompare8(src1, src2):
    //   Z=1 ⟺ src1==src2,  C=1 ⟺ src1>=src2,  C=0 ⟺ src1<src2
    // PIC12 has no SUBLW; both operands must be in registers for general compare.
    // -------------------------------------------------------------------------

    private void EmitCompare8(Val src1, Val src2)
    {
        int src1Addr = AddrOf(src1);
        int src2Addr = AddrOf(src2);

        if (src2 is Constant c2 && src1Addr >= 0)
        {
            // Load src2 into W, SUBWF src1 → W = src1 - src2.
            Emit("MOVLW", $"0x{c2.Value & 0xFF:X2}");
            Emit("SUBWF", Addr(src1Addr), "W");
        }
        else if (src1 is Constant c1 && src2Addr >= 0)
        {
            // PIC12 baseline has no SUBLW. Store c1 to TmpLo, then SUBWF src2 trick:
            // W = c1 - src2 requires: store c1 to TmpLo, load W=src2, SUBWF TmpLo, W
            // → W = TmpLo - W = c1 - src2 (WRONG: want src1 - src2 = c1 - src2 ✓ actually correct!)
            // Wait: SUBWF TmpLo, W = TmpLo - W_old = c1 - src2 = src1 - src2 ✓
            Emit("MOVLW", $"0x{c1.Value & 0xFF:X2}");
            Emit("MOVWF", Addr(TmpLo));
            Emit("MOVF",  Addr(src2Addr), "W");
            Emit("SUBWF", Addr(TmpLo), "W");  // W = TmpLo - W = c1 - src2 = src1 - src2 ✓
            // But wait, C flag: SUBWF f, W sets C=1 if f >= W (i.e., TmpLo >= src2, i.e., c1 >= src2 = src1 >= src2) ✓
        }
        else if (src1Addr >= 0 && src2Addr >= 0)
        {
            Emit("MOVF",  Addr(src2Addr), "W");
            Emit("SUBWF", Addr(src1Addr), "W");  // W = src1 - src2 ✓
        }
        else
        {
            // Both unresolved or both constants: load to temps.
            LoadIntoW(src1);
            Emit("MOVWF", Addr(TmpLo));
            LoadIntoW(src2);
            Emit("MOVWF", Addr(Tmp2Lo));
            Emit("MOVF",  Addr(Tmp2Lo), "W");
            Emit("SUBWF", Addr(TmpLo),  "W");  // W = TmpLo - Tmp2Lo = src1 - src2 ✓
        }
    }

    // Jump to target if Z=1.
    private void BranchIfEqual(string target)
    {
        Emit("BTFSC", Addr(StatusReg), $"{StatusZ}");
        Emit("GOTO",  target);
    }

    // Jump to target if Z=0.
    private void BranchIfNotEqual(string target)
    {
        Emit("BTFSS", Addr(StatusReg), $"{StatusZ}");
        Emit("GOTO",  target);
    }

    // Jump to target if C=0 (unsigned less-than).
    private void BranchIfLessThan(string target)
    {
        Emit("BTFSS", Addr(StatusReg), $"{StatusC}");  // C=1 → skip → don't jump
        Emit("GOTO",  target);
    }

    // Jump to target if C=1 (unsigned greater-or-equal).
    private void BranchIfGreaterEqual(string target)
    {
        Emit("BTFSC", Addr(StatusReg), $"{StatusC}");  // C=0 → skip → don't jump
        Emit("GOTO",  target);
    }

    // Jump to target if C=1 AND Z=0 (unsigned greater-than).
    private void BranchIfGreaterThan(string target)
    {
        string skip = MakeLabel("L12_GT_SK");
        Emit("BTFSC", Addr(StatusReg), $"{StatusZ}");  // Z=0 → skip GOTO skip
        Emit("GOTO",  skip);
        Emit("BTFSC", Addr(StatusReg), $"{StatusC}");  // C=0 → skip GOTO target
        Emit("GOTO",  target);
        EmitLabel(skip);
    }

    // Jump to target if C=0 OR Z=1 (unsigned less-or-equal).
    private void BranchIfLessOrEqual(string target)
    {
        Emit("BTFSC", Addr(StatusReg), $"{StatusZ}");  // Z=0 → skip GOTO target
        Emit("GOTO",  target);
        Emit("BTFSS", Addr(StatusReg), $"{StatusC}");  // C=1 → skip GOTO target
        Emit("GOTO",  target);
        // C=1 AND Z=0 → src1>src2 → fall through.
    }

    // -------------------------------------------------------------------------
    // top-level Compile
    // -------------------------------------------------------------------------

    public override void Compile(ProgramIR program, TextWriter output)
    {
        _assembly.Clear();
        _labelCounter = 0;

        var allocator = new StackAllocator();
        var (offsets, _) = allocator.Allocate(program);
        _varSizes = allocator.VariableSizes;

        _varAddrs.Clear();
        foreach (var (name, off) in offsets)
            _varAddrs[name] = VarBase + off;

        EmitComment($"Generated by pymcuc for {cfg.Chip} [WIP PIC12 backend — not production ready]");
        EmitRaw("");
        EmitRaw($"\tLIST P={cfg.Chip.ToUpperInvariant()}");
        EmitRaw("");

        foreach (var (name, addr) in _varAddrs)
            EmitRaw($"{name.Replace('.', '_')}\tEQU\t{Addr(addr)}");
        EmitRaw($"_tmp_lo\t\tEQU\t{Addr(TmpLo)}");
        EmitRaw($"_tmp_hi\t\tEQU\t{Addr(TmpHi)}");
        EmitRaw($"_retval_hi\tEQU\t{Addr(RetValHi)}");
        EmitRaw("");

        // Reset vector.
        EmitRaw("\tORG 0x0000");
        Emit("GOTO", "main");
        EmitRaw("");

        // Interrupt vector (PIC12F has interrupt at 0x0004; PIC10F has none).
        EmitRaw("\tORG 0x0004");
        var isrFuncs = program.Functions.Where(f => f.IsInterrupt).ToList();
        if (isrFuncs.Count > 0)
            Emit("GOTO", isrFuncs[0].Name);
        else
            Emit("RETFIE");
        EmitRaw("");

        EmitRaw("\tCODE");
        EmitRaw("");

        foreach (var func in isrFuncs) CompileFunction(func);
        foreach (var func in program.Functions.Where(f => !f.IsInterrupt && (!f.IsInline || f.Name == "main")))
            CompileFunction(func);

        EmitRaw("");
        EmitRaw("\tEND");

        foreach (var line in _assembly)
            output.WriteLine(line.ToString());
    }

    public override void EmitContextSave()
    {
        EmitComment("ISR context save (PIC12 — manual W/STATUS save)");
        Emit("MOVWF",  Addr(0x50));         // save W to dedicated area (device-specific)
        Emit("SWAPF",  Addr(StatusReg), "W");
        Emit("MOVWF",  Addr(0x51));         // save STATUS (swapped nibbles to avoid Z modification)
    }

    public override void EmitContextRestore()
    {
        EmitComment("ISR context restore (PIC12)");
        Emit("SWAPF",  Addr(0x51), "W");
        Emit("MOVWF",  Addr(StatusReg));
        Emit("SWAPF",  Addr(0x50), "F");
        Emit("SWAPF",  Addr(0x50), "W");
    }

    public override void EmitInterruptReturn() => Emit("RETFIE");

    // -------------------------------------------------------------------------
    // Function compilation
    // -------------------------------------------------------------------------

    private void CompileFunction(Function func)
    {
        EmitRaw("");
        EmitLabel(func.Name);

        if (func.IsInterrupt) EmitContextSave();

        if (!func.IsInterrupt && func.Name != "main" && func.Params.Count > 0)
        {
            int argOff = ArgBase;
            for (int k = 0; k < func.Params.Count; k++)
            {
                var pname = func.Params[k];
                int psz = _varSizes.TryGetValue(pname, out int s) ? s : 1;
                if (_varAddrs.TryGetValue(pname, out int paddr))
                {
                    Emit("MOVF",  Addr(argOff), "W");
                    Emit("MOVWF", Addr(paddr));
                }
                argOff += psz;
            }
        }

        bool emittedReturn = false;
        foreach (var instr in func.Body)
        {
            if (func.IsInterrupt && instr is Return)
            {
                EmitContextRestore();
                Emit("RETFIE");
                emittedReturn = true;
                continue;
            }
            CompileInstruction(instr);
        }

        if (func.IsInterrupt && !emittedReturn)
        {
            EmitContextRestore();
            Emit("RETFIE");
        }
    }

    // -------------------------------------------------------------------------
    // Instruction dispatch
    // -------------------------------------------------------------------------

    private void CompileInstruction(Instruction instr)
    {
        switch (instr)
        {
            case Return r:
                if (r.Value is not NoneVal) LoadIntoW(r.Value);
                Emit("RETURN");
                break;
            case Jump j: Emit("GOTO", j.Target); break;
            case JumpIfZero jz:
                LoadIntoW(jz.Condition);
                // MOVF sets Z if result is 0; MOVLW does NOT. Use MOVWF/MOVF to set Z:
                Emit("MOVWF", Addr(TmpLo));
                Emit("MOVF",  Addr(TmpLo), "F");  // sets Z
                Emit("BTFSC", Addr(StatusReg), $"{StatusZ}");
                Emit("GOTO",  jz.Target);
                break;
            case JumpIfNotZero jnz:
                LoadIntoW(jnz.Condition);
                Emit("MOVWF", Addr(TmpLo));
                Emit("MOVF",  Addr(TmpLo), "F");
                Emit("BTFSS", Addr(StatusReg), $"{StatusZ}");
                Emit("GOTO",  jnz.Target);
                break;
            case Label l: EmitLabel(l.Name); break;
            case DebugLine d:
                EmitComment(string.IsNullOrEmpty(d.SourceFile)
                    ? $"Line {d.Line}: {d.Text}"
                    : $"{d.SourceFile}:{d.Line}: {d.Text}");
                break;
            case JumpIfEqual je:
                EmitCompare8(je.Src1, je.Src2);  BranchIfEqual(je.Target);        break;
            case JumpIfNotEqual jne:
                EmitCompare8(jne.Src1, jne.Src2); BranchIfNotEqual(jne.Target);   break;
            case JumpIfLessThan jlt:
                EmitCompare8(jlt.Src1, jlt.Src2); BranchIfLessThan(jlt.Target);   break;
            case JumpIfLessOrEqual jle:
                EmitCompare8(jle.Src1, jle.Src2); BranchIfLessOrEqual(jle.Target);break;
            case JumpIfGreaterThan jgt:
                EmitCompare8(jgt.Src1, jgt.Src2); BranchIfGreaterThan(jgt.Target);break;
            case JumpIfGreaterOrEqual jge:
                EmitCompare8(jge.Src1, jge.Src2); BranchIfGreaterEqual(jge.Target);break;
            case Call c:    CompileCall(c);    break;
            case Copy cp:   CompileCopy(cp);   break;
            case Unary u:   CompileUnary(u);   break;
            case Binary b:  CompileBinary(b);  break;
            case BitSet bs:
            {
                int addr = AddrOf(bs.Target);
                if (addr >= 0) Emit("BSF", Addr(addr), $"{bs.Bit}");
                else EmitComment($"TODO(wip): BitSet on unresolved target {bs.Target}");
                break;
            }
            case BitClear bc:
            {
                int addr = AddrOf(bc.Target);
                if (addr >= 0) Emit("BCF", Addr(addr), $"{bc.Bit}");
                else EmitComment($"TODO(wip): BitClear on unresolved target {bc.Target}");
                break;
            }
            case BitCheck bck:
            {
                int srcAddr = AddrOf(bck.Source);
                string f = srcAddr >= 0 ? Addr(srcAddr) : Addr(TmpLo);
                if (srcAddr < 0) { LoadIntoW(bck.Source); Emit("MOVWF", Addr(TmpLo)); }
                string trueL = MakeLabel("L12_BCHK_T"); string doneL = MakeLabel("L12_BCHK_D");
                Emit("BTFSC", f, $"{bck.Bit}");
                Emit("GOTO",  trueL);
                Emit("CLRW");
                Emit("GOTO",  doneL);
                EmitLabel(trueL);
                Emit("MOVLW", "0x01");
                EmitLabel(doneL);
                StoreW(bck.Dst);
                break;
            }
            case BitWrite bw:
                EmitComment("TODO(wip): BitWrite — simplified implementation");
                LoadIntoW(bw.Src);
                {
                    int addr = AddrOf(bw.Target);
                    string f = addr >= 0 ? Addr(addr) : Addr(TmpLo);
                    if (addr < 0) { Emit("MOVWF", Addr(Tmp2Lo)); LoadIntoW(bw.Target); Emit("MOVWF", Addr(TmpLo)); Emit("MOVF", Addr(Tmp2Lo), "W"); f = Addr(TmpLo); }
                    Emit("MOVWF", Addr(TmpLo));
                    Emit("MOVF",  Addr(TmpLo), "F");
                    string setL = MakeLabel("L12_BW_S"); string doneL = MakeLabel("L12_BW_D");
                    Emit("BTFSS", Addr(StatusReg), $"{StatusZ}"); Emit("GOTO", setL);
                    Emit("BCF", f, $"{bw.Bit}"); Emit("GOTO", doneL);
                    EmitLabel(setL); Emit("BSF", f, $"{bw.Bit}");
                    EmitLabel(doneL);
                }
                break;
            case JumpIfBitSet jbs:
            {
                int addr = AddrOf(jbs.Source);
                string f = addr >= 0 ? Addr(addr) : Addr(TmpLo);
                if (addr < 0) { LoadIntoW(jbs.Source); Emit("MOVWF", Addr(TmpLo)); }
                Emit("BTFSC", f, $"{jbs.Bit}");
                Emit("GOTO",  jbs.Target);
                break;
            }
            case JumpIfBitClear jbc:
            {
                int addr = AddrOf(jbc.Source);
                string f = addr >= 0 ? Addr(addr) : Addr(TmpLo);
                if (addr < 0) { LoadIntoW(jbc.Source); Emit("MOVWF", Addr(TmpLo)); }
                Emit("BTFSS", f, $"{jbc.Bit}");
                Emit("GOTO",  jbc.Target);
                break;
            }
            case AugAssign aa: CompileAugAssign(aa); break;
            case InlineAsm asm2: EmitRaw(asm2.Code); break;
            case ArrayLoad al:
            {
                if (!_varAddrs.TryGetValue(al.ArrayName, out int baseAddr))
                    { EmitComment($"TODO(wip): ArrayLoad '{al.ArrayName}' not in var map"); break; }
                if (al.Index is Constant c)
                    { Emit("MOVF", Addr(baseAddr + c.Value * al.ElemType.SizeOf()), "W"); StoreW(al.Dst); }
                else
                    EmitComment("TODO(wip): variable-index ArrayLoad not supported on PIC12");
                break;
            }
            case ArrayStore ast:
            {
                if (!_varAddrs.TryGetValue(ast.ArrayName, out int baseAddr))
                    { EmitComment($"TODO(wip): ArrayStore '{ast.ArrayName}' not in var map"); break; }
                if (ast.Index is Constant c)
                    { LoadIntoW(ast.Src); Emit("MOVWF", Addr(baseAddr + c.Value * ast.ElemType.SizeOf())); }
                else
                    EmitComment("TODO(wip): variable-index ArrayStore not supported on PIC12");
                break;
            }
            case LoadIndirect li:
                EmitComment("TODO(wip): LoadIndirect — PIC12 FSR indirect");
                LoadIntoW(li.SrcPtr);
                Emit("MOVWF", Addr(0x04));  // FSR
                Emit("MOVF",  Addr(0x00), "W");  // INDF
                StoreW(li.Dst);
                break;
            case StoreIndirect si:
                EmitComment("TODO(wip): StoreIndirect — PIC12 FSR indirect");
                LoadIntoW(si.DstPtr);
                Emit("MOVWF", Addr(0x04));  // FSR
                LoadIntoW(si.Src);
                Emit("MOVWF", Addr(0x00));  // INDF
                break;
            case FlashData:
                EmitComment("TODO(wip): FlashData not supported on PIC12");
                break;
            case ArrayLoadFlash:
                EmitComment("TODO(wip): ArrayLoadFlash (RETLW table) not supported on PIC12");
                break;
        }
    }

    // -------------------------------------------------------------------------
    // Call (PIC12 CALL — only 8-bit call address; 2-level hardware stack)
    // -------------------------------------------------------------------------

    private void CompileCall(Call call)
    {
        int argOff = ArgBase;
        foreach (var arg in call.Args)
        {
            LoadIntoW(arg);
            Emit("MOVWF", Addr(argOff));
            argOff++;
        }
        Emit("CALL", call.FunctionName);
        if (call.Dst is not NoneVal)
            StoreW(call.Dst);
    }

    private void CompileCopy(Copy cp)
    {
        LoadIntoW(cp.Src);
        StoreW(cp.Dst);
    }

    // -------------------------------------------------------------------------
    // Unary  (8-bit only on PIC12)
    // -------------------------------------------------------------------------

    private void CompileUnary(Unary u)
    {
        if (GetValType(u.Dst).SizeOf() == 2)
        {
            EmitComment("TODO(wip): 16-bit Unary not supported on PIC12");
            return;
        }

        LoadIntoW(u.Src);
        switch (u.Op)
        {
            case IrUnOp.Neg:
                Emit("MOVWF", Addr(TmpLo));
                Emit("COMF",  Addr(TmpLo), "W");  // ~src
                Emit("ADDLW", "0x01");             // -src = ~src + 1
                break;
            case IrUnOp.BitNot:
                Emit("MOVWF", Addr(TmpLo));
                Emit("COMF",  Addr(TmpLo), "W");
                break;
            case IrUnOp.Not:
            {
                string trueL = MakeLabel("L12_NOT_T"); string doneL = MakeLabel("L12_NOT_D");
                Emit("MOVWF", Addr(TmpLo));
                Emit("MOVF",  Addr(TmpLo), "F");
                Emit("BTFSC", Addr(StatusReg), $"{StatusZ}");
                Emit("GOTO",  trueL);
                Emit("CLRW");
                Emit("GOTO",  doneL);
                EmitLabel(trueL);
                Emit("MOVLW", "0x01");
                EmitLabel(doneL);
                break;
            }
        }
        StoreW(u.Dst);
    }

    // -------------------------------------------------------------------------
    // Binary  (8-bit only on PIC12; 16-bit stubbed)
    // -------------------------------------------------------------------------

    private void CompileBinary(Binary b)
    {
        if (GetValType(b.Dst).SizeOf() == 2)
        {
            EmitComment($"TODO(wip): 16-bit Binary {b.Op} not supported on PIC12");
            return;
        }

        int src1Addr = AddrOf(b.Src1);
        int src2Addr = AddrOf(b.Src2);

        switch (b.Op)
        {
            case IrBinOp.Add:
                LoadIntoW(b.Src1);
                if (b.Src2 is Constant addK) Emit("ADDLW", $"0x{addK.Value & 0xFF:X2}");
                else { Emit("MOVWF", Addr(TmpLo)); LoadIntoW(b.Src2); Emit("ADDWF", Addr(TmpLo), "W"); }
                StoreW(b.Dst);
                break;

            case IrBinOp.Sub:
                // PIC12 has no SUBLW. Must use SUBWF.
                // W = src1 - src2: load W=src2, SUBWF src1_reg → W = src1 - src2.
                if (src1Addr >= 0)
                {
                    if (b.Src2 is Constant sk) Emit("MOVLW", $"0x{sk.Value & 0xFF:X2}");
                    else { Emit("MOVF", Addr(src2Addr >= 0 ? src2Addr : TmpLo), "W"); }
                    Emit("SUBWF", Addr(src1Addr), "W");  // W = src1 - src2 ✓
                }
                else
                {
                    EmitComment("TODO(wip): Sub with unresolved src1 on PIC12");
                    LoadIntoW(b.Src1);
                }
                StoreW(b.Dst);
                break;

            case IrBinOp.BitAnd:
                LoadIntoW(b.Src1);
                if (b.Src2 is Constant andK) Emit("ANDLW", $"0x{andK.Value & 0xFF:X2}");
                else { Emit("MOVWF", Addr(TmpLo)); LoadIntoW(b.Src2); Emit("ANDWF", Addr(TmpLo), "W"); }
                StoreW(b.Dst);
                break;

            case IrBinOp.BitOr:
                LoadIntoW(b.Src1);
                if (b.Src2 is Constant orK) Emit("IORLW", $"0x{orK.Value & 0xFF:X2}");
                else { Emit("MOVWF", Addr(TmpLo)); LoadIntoW(b.Src2); Emit("IORWF", Addr(TmpLo), "W"); }
                StoreW(b.Dst);
                break;

            case IrBinOp.BitXor:
                LoadIntoW(b.Src1);
                if (b.Src2 is Constant xorK) Emit("XORLW", $"0x{xorK.Value & 0xFF:X2}");
                else { Emit("MOVWF", Addr(TmpLo)); LoadIntoW(b.Src2); Emit("XORWF", Addr(TmpLo), "W"); }
                StoreW(b.Dst);
                break;

            case IrBinOp.LShift:
                if (b.Src2 is Constant ls)
                {
                    LoadIntoW(b.Src1);
                    for (int i = 0; i < (ls.Value & 7); i++)
                    {
                        Emit("MOVWF", Addr(TmpLo));
                        Emit("BCF",   Addr(StatusReg), "0");
                        Emit("RLF",   Addr(TmpLo), "W");
                    }
                }
                else { EmitComment("TODO(wip): variable LShift on PIC12"); LoadIntoW(b.Src1); }
                StoreW(b.Dst);
                break;

            case IrBinOp.RShift:
                if (b.Src2 is Constant rs)
                {
                    LoadIntoW(b.Src1);
                    for (int i = 0; i < (rs.Value & 7); i++)
                    {
                        Emit("MOVWF", Addr(TmpLo));
                        Emit("BCF",   Addr(StatusReg), "0");
                        Emit("RRF",   Addr(TmpLo), "W");
                    }
                }
                else { EmitComment("TODO(wip): variable RShift on PIC12"); LoadIntoW(b.Src1); }
                StoreW(b.Dst);
                break;

            case IrBinOp.Mul:
            case IrBinOp.Div:
            case IrBinOp.FloorDiv:
            case IrBinOp.Mod:
                EmitComment($"TODO(wip): {b.Op} requires software routine on PIC12");
                LoadIntoW(b.Src1);
                StoreW(b.Dst);
                break;

            case IrBinOp.Equal:
                EmitCompare8(b.Src1, b.Src2);
                EmitBoolFromZ(true, b.Dst); break;
            case IrBinOp.NotEqual:
                EmitCompare8(b.Src1, b.Src2);
                EmitBoolFromZ(false, b.Dst); break;
            case IrBinOp.LessThan:
                EmitCompare8(b.Src1, b.Src2);
                EmitBoolFromC(false, b.Dst); break;
            case IrBinOp.GreaterEqual:
                EmitCompare8(b.Src1, b.Src2);
                EmitBoolFromC(true, b.Dst); break;
            case IrBinOp.GreaterThan:
                EmitCompare8(b.Src1, b.Src2);
                EmitBoolGT(b.Dst); break;
            case IrBinOp.LessEqual:
                EmitCompare8(b.Src1, b.Src2);
                EmitBoolLE(b.Dst); break;
        }
    }

    private void EmitBoolFromZ(bool wantSet, Val dst)
    {
        string trueL = MakeLabel("L12_BZ_T"); string doneL = MakeLabel("L12_BZ_D");
        if (wantSet) { Emit("BTFSC", Addr(StatusReg), $"{StatusZ}"); Emit("GOTO", trueL); }
        else         { Emit("BTFSS", Addr(StatusReg), $"{StatusZ}"); Emit("GOTO", trueL); }
        Emit("CLRW"); Emit("GOTO", doneL);
        EmitLabel(trueL); Emit("MOVLW", "0x01");
        EmitLabel(doneL); StoreW(dst);
    }

    private void EmitBoolFromC(bool wantSet, Val dst)
    {
        string trueL = MakeLabel("L12_BC_T"); string doneL = MakeLabel("L12_BC_D");
        if (wantSet) { Emit("BTFSC", Addr(StatusReg), $"{StatusC}"); Emit("GOTO", trueL); }
        else         { Emit("BTFSS", Addr(StatusReg), $"{StatusC}"); Emit("GOTO", trueL); }
        Emit("CLRW"); Emit("GOTO", doneL);
        EmitLabel(trueL); Emit("MOVLW", "0x01");
        EmitLabel(doneL); StoreW(dst);
    }

    private void EmitBoolGT(Val dst)
    {
        string falseL = MakeLabel("L12_GT_F"); string doneL = MakeLabel("L12_GT_D");
        Emit("BTFSC", Addr(StatusReg), $"{StatusZ}"); Emit("GOTO", falseL);  // equal → false
        Emit("BTFSS", Addr(StatusReg), $"{StatusC}"); Emit("GOTO", falseL);  // C=0 → false
        Emit("MOVLW", "0x01"); Emit("GOTO", doneL);
        EmitLabel(falseL); Emit("CLRW");
        EmitLabel(doneL); StoreW(dst);
    }

    private void EmitBoolLE(Val dst)
    {
        string trueL = MakeLabel("L12_LE_T"); string doneL = MakeLabel("L12_LE_D");
        Emit("BTFSC", Addr(StatusReg), $"{StatusZ}"); Emit("GOTO", trueL);   // equal → true
        Emit("BTFSS", Addr(StatusReg), $"{StatusC}"); Emit("GOTO", trueL);   // C=0 → true
        Emit("CLRW"); Emit("GOTO", doneL);
        EmitLabel(trueL); Emit("MOVLW", "0x01");
        EmitLabel(doneL); StoreW(dst);
    }

    // -------------------------------------------------------------------------
    // AugAssign  (8-bit; 16-bit stubbed)
    // -------------------------------------------------------------------------

    private void CompileAugAssign(AugAssign aa)
    {
        if (_varSizes.TryGetValue(
                aa.Target switch { Variable v => v.Name, Temporary t => t.Name, _ => "" },
                out int sz) && sz == 2)
        {
            EmitComment($"TODO(wip): 16-bit AugAssign {aa.Op} not supported on PIC12");
            return;
        }

        int dstAddr = AddrOf(aa.Target);
        if (dstAddr >= 0)
        {
            switch (aa.Op)
            {
                case IrBinOp.Add when aa.Operand is Constant { Value: 1 }:
                    Emit("INCF", Addr(dstAddr), "F"); return;
                case IrBinOp.Sub when aa.Operand is Constant { Value: 1 }:
                    Emit("DECF", Addr(dstAddr), "F"); return;
                case IrBinOp.Add:
                    LoadIntoW(aa.Operand); Emit("ADDWF", Addr(dstAddr), "F"); return;
                case IrBinOp.BitAnd:
                    LoadIntoW(aa.Operand); Emit("ANDWF", Addr(dstAddr), "F"); return;
                case IrBinOp.BitOr:
                    LoadIntoW(aa.Operand); Emit("IORWF", Addr(dstAddr), "F"); return;
                case IrBinOp.BitXor:
                    LoadIntoW(aa.Operand); Emit("XORWF", Addr(dstAddr), "F"); return;
                default: break;
            }
        }

        LoadIntoW(aa.Target);
        Emit("MOVWF", Addr(TmpLo));
        LoadIntoW(aa.Operand);
        switch (aa.Op)
        {
            case IrBinOp.Add:    Emit("ADDWF", Addr(TmpLo), "W"); break;
            case IrBinOp.Sub:    Emit("SUBWF", Addr(TmpLo), "W"); break;
            case IrBinOp.BitAnd: Emit("ANDWF", Addr(TmpLo), "W"); break;
            case IrBinOp.BitOr:  Emit("IORWF", Addr(TmpLo), "W"); break;
            case IrBinOp.BitXor: Emit("XORWF", Addr(TmpLo), "W"); break;
            default:
                EmitComment($"TODO(wip): AugAssign {aa.Op} not implemented on PIC12");
                break;
        }
        StoreW(aa.Target);
    }
}
