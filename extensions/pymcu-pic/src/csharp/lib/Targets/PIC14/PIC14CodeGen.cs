/*
 * -----------------------------------------------------------------------------
 * PyMCU Compiler (pymcuc) — PIC14 (Midrange) Backend  [Experimental]
 * Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
 *
 * SPDX-License-Identifier: MIT
 *
 * -----------------------------------------------------------------------------
 * STATUS: Experimental.  No cycle-accurate PIC14 simulator is integrated
 * into the test suite.  Use with caution.
 * -----------------------------------------------------------------------------
 *
 * Architecture notes (PIC14 / PIC16F midrange):
 *   - 14-bit instruction words.
 *   - W register is the accumulator; no BZ/BNZ/BC/BNC branch instructions.
 *   - STATUS register at 0x03: C=bit0, DC=bit1, Z=bit2.
 *   - Conditional branches use BTFSS/BTFSC STATUS, bit + GOTO.
 *   - GOTO and CALL are 11-bit; for larger programs, PAGESEL support is opt-in
 *     via 'multipage = true' in [tool.pymcu.fuses].
 *   - Data memory banks: Bank 0 GPRs 0x20-0x7F (96 bytes).
 *   - No ADDWFC/SUBWFB; 16-bit arithmetic done byte-by-byte with carry.
 *   - MULWF / MULLW do not exist; software multiply required.
 *
 * Memory layout:
 *   0x20-0x6B  User variables (76 bytes; VarBase = 0x20).
 *   0x6C       TMP_LO  — 8-bit scratch / low byte of 16-bit temp.
 *   0x6D       TMP_HI  — high byte of 16-bit temp.
 *   0x6E       TMP2_LO — secondary scratch low byte.
 *   0x6F       TMP2_HI — secondary scratch high byte.
 *   0x70       RETVAL_HI — high byte of 16-bit return value.
 *   0x71-0x7F  ARG_BASE — parameter-passing area (15 bytes max).
 *   (0x7A-0x7B  ISR W/STATUS save; 0x7C  ISR FSR save — within ARG_BASE range,
 *    so max effective arg area is 0x71-0x79 = 9 bytes / 4 args.)
 *
 * Calling convention:
 *   - Arguments in ARG_BASE area; 8-bit return in W; 16-bit return W (lo) + RETVAL_HI.
 * -----------------------------------------------------------------------------
 */

using PyMCU.Backend.Analysis;
using PyMCU.Backend.Targets.PIC;
using PyMCU.Common.Models;
using PyMCU.IR;
using IrBinOp = PyMCU.IR.BinaryOp;
using IrUnOp  = PyMCU.IR.UnaryOp;

namespace PyMCU.Backend.Targets.PIC14;

public class PIC14CodeGen(DeviceConfig cfg) : CodeGen
{
    private readonly List<PicAsmLine> _assembly = [];
    private Dictionary<string, int>  _varAddrs  = new();
    private Dictionary<string, int>  _varSizes  = new();
    private Dictionary<string, int>  _funcArgSizes = new();
    private int                      _labelCounter;
    private Function?                _currentFunction;

    // Opt-in features from device config fuses.
    private readonly bool _multiPage =
        cfg.Fuses.TryGetValue("multipage", out string? mp) && mp == "true";

    // Subroutine emission flags (emit once per compilation unit).
    private bool _needsDiv8  = false;
    private bool _needsDiv16 = false;

    // STATUS register constants (PIC14, Bank 0).
    private const int StatusReg = 0x03;
    private const int StatusC   = 0;      // Carry bit
    private const int StatusZ   = 2;      // Zero bit

    // Scratch area (end of Bank 0 GPR space).
    private const int TmpLo    = 0x6C;
    private const int TmpHi    = 0x6D;
    private const int Tmp2Lo   = 0x6E;
    private const int Tmp2Hi   = 0x6F;
    private const int RetValHi = 0x70;
    private const int ArgBase  = 0x71;
    private const int VarBase  = 0x20;

    // ISR context save area.
    private const int WsaveReg      = 0x7A;
    private const int StatusSaveReg = 0x7B;
    private const int FsrSaveReg    = 0x7C;  // Save area for FSR during ISR

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
        Constant { Value: > 255 or < -128 } => DataType.UINT16,
        _ => DataType.UINT8,
    };

    private static bool Is16(DataType t) => t.SizeOf() == 2;

    private int AddrOf(Val val)
    {
        var name = val switch { Variable v => v.Name, Temporary t => t.Name, _ => "" };
        return !string.IsNullOrEmpty(name) && _varAddrs.TryGetValue(name, out int a) ? a : -1;
    }

    private bool Is16Bit(Val val)
    {
        var name = val switch { Variable v => v.Name, Temporary t => t.Name, _ => "" };
        if (!string.IsNullOrEmpty(name) && _varSizes.TryGetValue(name, out int sz)) return sz == 2;
        return Is16(GetValType(val));
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
            EmitComment($"TODO(wip): unresolved 8-bit load for {val}");
    }

    private void StoreW(Val dst)
    {
        if (dst is Constant) return;
        if (dst is MemoryAddress mem)
        {
            Emit("MOVWF", Addr(mem.Address));
            return;
        }
        int addr = AddrOf(dst);
        if (addr >= 0)
            Emit("MOVWF", Addr(addr));
        else
            EmitComment($"TODO(wip): unresolved 8-bit store for {dst}");
    }

    private void Load16Into(Val val, int lo, int hi)
    {
        switch (val)
        {
            case Constant c:
                Emit("MOVLW", $"0x{c.Value & 0xFF:X2}");
                Emit("MOVWF", Addr(lo));
                Emit("MOVLW", $"0x{(c.Value >> 8) & 0xFF:X2}");
                Emit("MOVWF", Addr(hi));
                return;
            case MemoryAddress mem:
                Emit("MOVF", Addr(mem.Address),     "W"); Emit("MOVWF", Addr(lo));
                Emit("MOVF", Addr(mem.Address + 1), "W"); Emit("MOVWF", Addr(hi));
                return;
        }
        int addr = AddrOf(val);
        if (addr >= 0)
        {
            Emit("MOVF", Addr(addr),     "W"); Emit("MOVWF", Addr(lo));
            Emit("MOVF", Addr(addr + 1), "W"); Emit("MOVWF", Addr(hi));
        }
        else EmitComment($"TODO(wip): unresolved 16-bit load for {val}");
    }

    private void Store16From(int lo, int hi, Val dst)
    {
        if (dst is Constant) return;
        if (dst is MemoryAddress mem)
        {
            Emit("MOVF", Addr(lo), "W"); Emit("MOVWF", Addr(mem.Address));
            Emit("MOVF", Addr(hi), "W"); Emit("MOVWF", Addr(mem.Address + 1));
            return;
        }
        int addr = AddrOf(dst);
        if (addr >= 0)
        {
            Emit("MOVF", Addr(lo), "W"); Emit("MOVWF", Addr(addr));
            Emit("MOVF", Addr(hi), "W"); Emit("MOVWF", Addr(addr + 1));
        }
        else EmitComment($"TODO(wip): unresolved 16-bit store for {dst}");
    }

    // -------------------------------------------------------------------------
    // Compare: same convention as PIC18 backend.
    //   After EmitCompare8(src1, src2):
    //     Z=1  ⟺  src1 == src2
    //     C=1  ⟺  src1 >= src2  (unsigned)
    //     C=0  ⟺  src1  < src2
    // -------------------------------------------------------------------------

    private void EmitCompare8(Val src1, Val src2)
    {
        int src1Addr = AddrOf(src1);
        int src2Addr = AddrOf(src2);

        if (src2 is Constant c2)
        {
            Emit("MOVLW", $"0x{c2.Value & 0xFF:X2}");
            if (src1 is Constant c1)
                Emit("SUBLW", $"0x{c1.Value & 0xFF:X2}");  // W = c1 - c2
            else if (src1Addr >= 0)
                Emit("SUBWF", Addr(src1Addr), "W");         // W = src1 - c2
            else
            {
                Emit("MOVWF", Addr(TmpLo));
                LoadIntoW(src1);
                Emit("SUBWF", Addr(TmpLo), "W");
                EmitComment("TODO(wip): compare fallback may produce inverted carry");
            }
        }
        else if (src1 is Constant c1v)
        {
            if (src2Addr >= 0)
                Emit("MOVF", Addr(src2Addr), "W");
            else
                LoadIntoW(src2);
            Emit("SUBLW", $"0x{c1v.Value & 0xFF:X2}");      // W = c1v - src2 = src1 - src2
        }
        else if (src1Addr >= 0 && src2Addr >= 0)
        {
            Emit("MOVF",  Addr(src2Addr), "W");
            Emit("SUBWF", Addr(src1Addr), "W");              // W = src1 - src2
        }
        else
        {
            Load16Into(src1, TmpLo, TmpHi);
            Load16Into(src2, Tmp2Lo, Tmp2Hi);
            Emit("MOVF",  Addr(Tmp2Lo), "W");
            Emit("SUBWF", Addr(TmpLo),  "W");
        }
    }

    // -------------------------------------------------------------------------
    // PIC14 conditional branches via BTFSS/BTFSC STATUS + GOTO
    //
    // BTFSC f, b — skip next instruction if bit b of f is CLEAR (=0).
    // BTFSS f, b — skip next instruction if bit b of f is SET   (=1).
    //
    // "Jump to target if Z=1":
    //   BTFSC STATUS, Z  ; Z=0 → skip the GOTO  → don't jump
    //   GOTO target      ; Z=1 → execute GOTO   → jump
    // -------------------------------------------------------------------------

    // Jump to `target` if Z=1.
    private void BranchIfEqual(string target)
    {
        Emit("BTFSC", Addr(StatusReg), $"{StatusZ}");
        Emit("GOTO",  target);
    }

    // Jump to `target` if Z=0.
    private void BranchIfNotEqual(string target)
    {
        Emit("BTFSS", Addr(StatusReg), $"{StatusZ}");
        Emit("GOTO",  target);
    }

    // Jump to `target` if C=0 (src1 < src2, unsigned).
    private void BranchIfLessThan(string target)
    {
        Emit("BTFSS", Addr(StatusReg), $"{StatusC}");  // skip if C=1 (src1>=src2)
        Emit("GOTO",  target);
    }

    // Jump to `target` if C=1 (src1 >= src2, unsigned).
    private void BranchIfGreaterEqual(string target)
    {
        Emit("BTFSC", Addr(StatusReg), $"{StatusC}");  // skip if C=0 (src1<src2)
        Emit("GOTO",  target);
    }

    // Jump to `target` if C=1 AND Z=0 (src1 > src2, unsigned).
    private void BranchIfGreaterThan(string target)
    {
        string skip = MakeLabel("L14_GT_SK");
        Emit("BTFSC", Addr(StatusReg), $"{StatusZ}");  // Z=1 (equal) → skip GOTO skip → proceed to C check? No...
        // BTFSC STATUS, Z: if Z=CLEAR (Z=0), skip next → skip GOTO skip
        // If Z=1 (equal), don't skip → execute GOTO skip (don't jump to target)
        Emit("GOTO",  skip);                            // Z=1 → equal → don't jump
        // Z=0 here.
        Emit("BTFSC", Addr(StatusReg), $"{StatusC}");  // if C=0 → skip → don't jump
        Emit("GOTO",  target);                          // C=1 AND Z=0 → src1>src2 → jump
        EmitLabel(skip);
    }

    // Jump to `target` if C=0 OR Z=1 (src1 <= src2, unsigned).
    private void BranchIfLessOrEqual(string target)
    {
        // Z=1 (equal) → jump immediately.
        Emit("BTFSC", Addr(StatusReg), $"{StatusZ}");  // Z=0 → skip; Z=1 → proceed
        Emit("GOTO",  target);                          // Z=1 → jump
        // Z=0 here; now check C.
        Emit("BTFSS", Addr(StatusReg), $"{StatusC}");  // C=1 → skip → don't jump; C=0 → proceed
        Emit("GOTO",  target);                          // C=0 AND Z=0 → src1<src2 → jump
        // C=1 AND Z=0: src1 > src2 → fall through (don't jump).
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

        // File header.
        EmitComment($"Generated by pymcuc for {cfg.Chip} [Experimental PIC14 backend]");
        EmitComment("[WARNING] PIC14 is Experimental. Limited to Bank 0 (76 bytes); single-page default.");
        EmitRaw("");
        EmitRaw($"\tLIST P={cfg.Chip.ToUpperInvariant()}");
        EmitRaw("");

        // EQU directives.
        foreach (var (name, addr) in _varAddrs)
            EmitRaw($"{name.Replace('.', '_')}\tEQU\t{Addr(addr)}");
        EmitRaw($"_tmp_lo\t\tEQU\t{Addr(TmpLo)}");
        EmitRaw($"_tmp_hi\t\tEQU\t{Addr(TmpHi)}");
        EmitRaw($"_tmp2_lo\tEQU\t{Addr(Tmp2Lo)}");
        EmitRaw($"_tmp2_hi\tEQU\t{Addr(Tmp2Hi)}");
        EmitRaw($"_retval_hi\tEQU\t{Addr(RetValHi)}");
        EmitRaw("");

        // Reset vector.
        EmitRaw("\tORG 0x0000");
        Emit("GOTO", "main");
        EmitRaw("");

        // Interrupt vector.
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

        // Emit flash data tables (RETLW sequences) collected during function compilation.
        EmitFlashTables(program);

        // Emit software divide subroutines if needed.
        if (_needsDiv8)  EmitDiv8Subroutine();
        if (_needsDiv16) EmitDiv16Subroutine();

        EmitRaw("");
        EmitRaw("\tEND");

        foreach (var line in _assembly)
            output.WriteLine(line.ToString());
    }

    public override void EmitContextSave()
    {
        // PIC14 has no shadow registers; save W/STATUS manually.
        // WsaveReg and StatusSaveReg are reserved above the user variable space.
        EmitComment("ISR context save: push W and STATUS to reserved save area");
        Emit("MOVWF",  Addr(WsaveReg));              // save W to dedicated save area
        Emit("SWAPF",  Addr(StatusReg), "W");        // STATUS → W via SWAPF to avoid affecting STATUS
        Emit("MOVWF",  Addr(StatusSaveReg));         // save STATUS

        // Conditionally save FSR if the ISR body uses indirect access or variable array access.
        bool savesFsr = _currentFunction != null && _currentFunction.Body.Any(i =>
            i is LoadIndirect or StoreIndirect or
            ArrayLoad { Index: not Constant } or
            ArrayStore { Index: not Constant });
        if (savesFsr)
        {
            EmitComment("ISR uses indirect access: save FSR");
            Emit("MOVF",  Addr(0x04), "W");          // W = FSR
            Emit("MOVWF", Addr(FsrSaveReg));         // FsrSaveReg = FSR
        }
    }

    public override void EmitContextRestore()
    {
        // Restore FSR first if it was saved.
        bool savesFsr = _currentFunction != null && _currentFunction.Body.Any(i =>
            i is LoadIndirect or StoreIndirect or
            ArrayLoad { Index: not Constant } or
            ArrayStore { Index: not Constant });
        if (savesFsr)
        {
            EmitComment("ISR uses indirect access: restore FSR");
            Emit("MOVF",  Addr(FsrSaveReg), "W");   // W = saved FSR
            Emit("MOVWF", Addr(0x04));               // FSR = saved value
        }

        EmitComment("ISR context restore");
        Emit("SWAPF",  Addr(StatusSaveReg), "W");   // restore STATUS (via SWAPF to avoid Z corruption)
        Emit("MOVWF",  Addr(StatusReg));             // write to STATUS
        Emit("SWAPF",  Addr(WsaveReg), "F");         // swap W save in place
        Emit("SWAPF",  Addr(WsaveReg), "W");         // restore original W without corrupting STATUS
    }

    public override void EmitInterruptReturn() => Emit("RETFIE");

    // -------------------------------------------------------------------------
    // Function compilation
    // -------------------------------------------------------------------------

    private void CompileFunction(Function func)
    {
        _currentFunction = func;
        EmitRaw("");
        EmitLabel(func.Name);

        if (func.IsInterrupt) EmitContextSave();

        if (func.Name == "main")
        {
            EmitComment("PIC14 startup");
        }

        // Copy arguments from ARG_BASE into local slots.
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
                    if (psz == 2)
                    {
                        Emit("MOVF",  Addr(argOff + 1), "W");
                        Emit("MOVWF", Addr(paddr + 1));
                    }
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
            case Return r:          CompileReturn(r);          break;
            case Jump j:            Emit("GOTO", j.Target);    break;
            case JumpIfZero jz:     CompileJumpIfZero(jz);     break;
            case JumpIfNotZero jnz: CompileJumpIfNotZero(jnz); break;
            case Label l:           EmitLabel(l.Name);         break;
            case DebugLine d:
                EmitComment(string.IsNullOrEmpty(d.SourceFile)
                    ? $"Line {d.Line}: {d.Text}"
                    : $"{d.SourceFile}:{d.Line}: {d.Text}");
                break;
            case JumpIfEqual je:
                EmitCompare8(je.Src1, je.Src2);
                BranchIfEqual(je.Target);
                break;
            case JumpIfNotEqual jne:
                EmitCompare8(jne.Src1, jne.Src2);
                BranchIfNotEqual(jne.Target);
                break;
            case JumpIfLessThan jlt:
            {
                bool signed = GetValType(jlt.Src1).IsSigned() || GetValType(jlt.Src2).IsSigned();
                if (signed)
                    BranchIfLessThanSigned(jlt.Src1, jlt.Src2, jlt.Target);
                else
                { EmitCompare8(jlt.Src1, jlt.Src2); BranchIfLessThan(jlt.Target); }
                break;
            }
            case JumpIfLessOrEqual jle:
            {
                bool signed = GetValType(jle.Src1).IsSigned() || GetValType(jle.Src2).IsSigned();
                if (signed)
                    BranchIfLessOrEqualSigned(jle.Src1, jle.Src2, jle.Target);
                else
                { EmitCompare8(jle.Src1, jle.Src2); BranchIfLessOrEqual(jle.Target); }
                break;
            }
            case JumpIfGreaterThan jgt:
            {
                bool signed = GetValType(jgt.Src1).IsSigned() || GetValType(jgt.Src2).IsSigned();
                if (signed)
                    BranchIfGreaterThanSigned(jgt.Src1, jgt.Src2, jgt.Target);
                else
                { EmitCompare8(jgt.Src1, jgt.Src2); BranchIfGreaterThan(jgt.Target); }
                break;
            }
            case JumpIfGreaterOrEqual jge:
            {
                bool signed = GetValType(jge.Src1).IsSigned() || GetValType(jge.Src2).IsSigned();
                if (signed)
                    BranchIfGreaterEqualSigned(jge.Src1, jge.Src2, jge.Target);
                else
                { EmitCompare8(jge.Src1, jge.Src2); BranchIfGreaterEqual(jge.Target); }
                break;
            }
            case Call c:             CompileCall(c);            break;
            case Copy cp:            CompileCopy(cp);           break;
            case LoadIndirect li:    CompileLoadIndirect(li);   break;
            case StoreIndirect si:   CompileStoreIndirect(si);  break;
            case Unary u:            CompileUnary(u);           break;
            case Binary b:           CompileBinary(b);          break;
            case BitSet bs:          CompileBitSet(bs);         break;
            case BitClear bc:        CompileBitClear(bc);       break;
            case BitCheck bck:       CompileBitCheck(bck);      break;
            case BitWrite bw:        CompileBitWrite(bw);       break;
            case JumpIfBitSet jbs:   CompileJumpIfBitSet(jbs);  break;
            case JumpIfBitClear jbc: CompileJumpIfBitClear(jbc);break;
            case AugAssign aa:       CompileAugAssign(aa);      break;
            case InlineAsm asm2:     EmitRaw(asm2.Code);        break;
            case ArrayLoad al:       CompileArrayLoad(al);      break;
            case ArrayStore ast:     CompileArrayStore(ast);    break;
            case FlashData fd:
                // RETLW table will be emitted after all functions; no inline code needed.
                break;
            case ArrayLoadFlash alf:
                CompileArrayLoadFlash(alf);
                break;
        }
    }

    // -------------------------------------------------------------------------
    // Return
    // -------------------------------------------------------------------------

    private void CompileReturn(Return r)
    {
        if (r.Value is not NoneVal)
        {
            if (Is16Bit(r.Value))
            {
                Load16Into(r.Value, TmpLo, TmpHi);
                Emit("MOVF",  Addr(TmpHi), "W");
                Emit("MOVWF", Addr(RetValHi));
                Emit("MOVF",  Addr(TmpLo), "W");  // W = lo byte (caller reads lo from W)
            }
            else
            {
                LoadIntoW(r.Value);
            }
        }
        Emit("RETURN");
    }

    // -------------------------------------------------------------------------
    // Conditional zero checks
    // -------------------------------------------------------------------------

    private void CompileJumpIfZero(JumpIfZero jz)
    {
        if (Is16Bit(jz.Condition))
        {
            Load16Into(jz.Condition, TmpLo, TmpHi);
            // OR lo and hi; Z=1 if both are zero (using IORWF).
            Emit("MOVF",  Addr(TmpLo), "W");
            Emit("IORWF", Addr(TmpHi), "W");  // W = lo | hi; updates Z
        }
        else if (jz.Condition is Constant c)
        {
            // MOVLW does not update Z; store to TmpLo and MOVF to set Z.
            Emit("MOVLW", $"0x{c.Value & 0xFF:X2}");
            Emit("MOVWF", Addr(TmpLo));
            Emit("MOVF",  Addr(TmpLo), "F");  // sets Z
        }
        else
        {
            LoadIntoW(jz.Condition);  // MOVF addr, W sets Z for Variable/Temporary/MemoryAddress
        }
        Emit("BTFSC", Addr(StatusReg), $"{StatusZ}");  // Z=0 → skip GOTO → don't jump
        Emit("GOTO",  jz.Target);
    }

    private void CompileJumpIfNotZero(JumpIfNotZero jnz)
    {
        if (Is16Bit(jnz.Condition))
        {
            Load16Into(jnz.Condition, TmpLo, TmpHi);
            Emit("MOVF",  Addr(TmpLo), "W");
            Emit("IORWF", Addr(TmpHi), "W");
        }
        else if (jnz.Condition is Constant c)
        {
            Emit("MOVLW", $"0x{c.Value & 0xFF:X2}");
            Emit("MOVWF", Addr(TmpLo));
            Emit("MOVF",  Addr(TmpLo), "F");
        }
        else
        {
            LoadIntoW(jnz.Condition);
        }
        Emit("BTFSS", Addr(StatusReg), $"{StatusZ}");  // Z=1 → skip GOTO → don't jump
        Emit("GOTO",  jnz.Target);
    }

    // -------------------------------------------------------------------------
    // Call
    // -------------------------------------------------------------------------

    private void CompileCall(Call call)
    {
        int argOff = ArgBase;
        foreach (var arg in call.Args)
        {
            if (Is16Bit(arg))
            {
                Load16Into(arg, TmpLo, TmpHi);
                Emit("MOVF",  Addr(TmpLo), "W"); Emit("MOVWF", Addr(argOff));
                Emit("MOVF",  Addr(TmpHi), "W"); Emit("MOVWF", Addr(argOff + 1));
                argOff += 2;
            }
            else
            {
                LoadIntoW(arg);
                Emit("MOVWF", Addr(argOff));
                argOff++;
            }
        }

        if (_multiPage)
            Emit("PAGESEL", call.FunctionName);

        Emit("CALL", call.FunctionName);

        if (call.Dst is not NoneVal)
        {
            if (Is16Bit(call.Dst))
            {
                Emit("MOVWF", Addr(TmpLo));
                Emit("MOVF",  Addr(RetValHi), "W");
                Emit("MOVWF", Addr(TmpHi));
                Store16From(TmpLo, TmpHi, call.Dst);
            }
            else
            {
                StoreW(call.Dst);
            }
        }
    }

    // -------------------------------------------------------------------------
    // Copy
    // -------------------------------------------------------------------------

    private void CompileCopy(Copy cp)
    {
        if (Is16Bit(cp.Dst) || Is16Bit(cp.Src))
        {
            Load16Into(cp.Src, TmpLo, TmpHi);
            Store16From(TmpLo, TmpHi, cp.Dst);
        }
        else
        {
            LoadIntoW(cp.Src);
            StoreW(cp.Dst);
        }
    }

    // -------------------------------------------------------------------------
    // Indirect load / store (FSR-based; PIC14 has a single FSR at 0x04/0x84)
    // -------------------------------------------------------------------------

    private const int FsrReg  = 0x04;  // FSR (File Select Register)
    private const int IndfReg = 0x00;  // INDF

    private void CompileLoadIndirect(LoadIndirect li)
    {
        LoadIntoW(li.SrcPtr);
        Emit("MOVWF", Addr(FsrReg));            // FSR = address
        Emit("MOVF",  Addr(IndfReg), "W");      // W = [FSR]
        StoreW(li.Dst);
    }

    private void CompileStoreIndirect(StoreIndirect si)
    {
        LoadIntoW(si.DstPtr);
        Emit("MOVWF", Addr(FsrReg));
        LoadIntoW(si.Src);
        Emit("MOVWF", Addr(IndfReg));           // [FSR] = W
    }

    // -------------------------------------------------------------------------
    // Unary
    // -------------------------------------------------------------------------

    private void CompileUnary(Unary u)
    {
        bool is16 = Is16Bit(u.Dst);

        if (is16)
        {
            Load16Into(u.Src, TmpLo, TmpHi);
            switch (u.Op)
            {
                case IrUnOp.Neg:
                    // Two's complement: ~lo + 1 : ~hi + carry from lo.
                    // COMF both bytes first, then INCF lo. If lo wraps to 0 (Z=1), propagate carry to hi.
                    Emit("COMF",  Addr(TmpLo), "F");
                    Emit("COMF",  Addr(TmpHi), "F");
                    Emit("INCF",  Addr(TmpLo), "F");    // lo++; Z=1 if lo wrapped to 0 (carry)
                    Emit("BTFSC", Addr(StatusReg), $"{StatusZ}");  // Z=0 → skip; Z=1 → carry into hi
                    Emit("INCF",  Addr(TmpHi), "F");
                    break;
                case IrUnOp.BitNot:
                    Emit("COMF", Addr(TmpLo), "F");
                    Emit("COMF", Addr(TmpHi), "F");
                    break;
                case IrUnOp.Not:
                    Emit("MOVF",  Addr(TmpLo), "W");
                    Emit("IORWF", Addr(TmpHi), "W");  // Z=1 if both zero
                    // If Z=1: result is 1, else 0.
                    string notTrueL16 = MakeLabel("L14_NOT16_T");
                    string notDoneL16 = MakeLabel("L14_NOT16_D");
                    Emit("BTFSC", Addr(StatusReg), $"{StatusZ}");
                    Emit("GOTO",  notTrueL16);
                    Emit("CLRW");
                    Emit("GOTO",  notDoneL16);
                    EmitLabel(notTrueL16);
                    Emit("MOVLW", "0x01");
                    EmitLabel(notDoneL16);
                    Emit("MOVWF", Addr(TmpLo));
                    Emit("CLRF",  Addr(TmpHi));
                    break;
            }
            Store16From(TmpLo, TmpHi, u.Dst);
            return;
        }

        LoadIntoW(u.Src);
        switch (u.Op)
        {
            case IrUnOp.Neg:
                Emit("MOVWF", Addr(TmpLo));
                Emit("COMF",  Addr(TmpLo), "W");  // ~src
                Emit("ADDLW", "0x01");             // ~src + 1 = -src
                break;
            case IrUnOp.BitNot:
                Emit("MOVWF", Addr(TmpLo));
                Emit("COMF",  Addr(TmpLo), "W");
                break;
            case IrUnOp.Not:
            {
                string trueL = MakeLabel("L14_NOT_T");
                string doneL = MakeLabel("L14_NOT_D");
                Emit("MOVWF", Addr(TmpLo));
                Emit("MOVF",  Addr(TmpLo), "F");   // sets Z
                Emit("BTFSC", Addr(StatusReg), $"{StatusZ}"); // Z=0 → skip
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
    // Binary
    // -------------------------------------------------------------------------

    private void CompileBinary(Binary b)
    {
        if (Is16(GetValType(b.Dst))) { CompileBinary16(b); return; }

        int dstAddr  = AddrOf(b.Dst);
        int src1Addr = AddrOf(b.Src1);

        switch (b.Op)
        {
            case IrBinOp.Add:
                LoadIntoW(b.Src1);
                Emit("MOVWF", Addr(TmpLo));
                LoadIntoW(b.Src2);
                Emit("ADDWF", Addr(TmpLo), "W");  // W = TmpLo + W = src1 + src2
                StoreW(b.Dst);
                break;

            case IrBinOp.Sub:
                if (b.Src2 is Constant sk && src1Addr >= 0)
                {
                    Emit("MOVLW", $"0x{sk.Value & 0xFF:X2}");
                    Emit("SUBWF", Addr(src1Addr), "W");  // W = src1 - src2 ✓
                }
                else if (b.Src1 is Constant sk1)
                {
                    LoadIntoW(b.Src2);
                    Emit("SUBLW", $"0x{sk1.Value & 0xFF:X2}");  // W = sk1 - src2
                }
                else
                {
                    // General case: load src1 into TmpLo, load src2 into W, SUBWF TmpLo → W = TmpLo - W
                    LoadIntoW(b.Src1);
                    Emit("MOVWF", Addr(TmpLo));
                    LoadIntoW(b.Src2);
                    Emit("SUBWF", Addr(TmpLo), "W");  // W = TmpLo - W = src1 - src2 ✓
                }
                StoreW(b.Dst);
                break;

            case IrBinOp.BitAnd:
                LoadIntoW(b.Src1);
                Emit("MOVWF", Addr(TmpLo));
                LoadIntoW(b.Src2);
                Emit("ANDWF", Addr(TmpLo), "W");
                StoreW(b.Dst);
                break;

            case IrBinOp.BitOr:
                LoadIntoW(b.Src1);
                Emit("MOVWF", Addr(TmpLo));
                LoadIntoW(b.Src2);
                Emit("IORWF", Addr(TmpLo), "W");
                StoreW(b.Dst);
                break;

            case IrBinOp.BitXor:
                LoadIntoW(b.Src1);
                Emit("MOVWF", Addr(TmpLo));
                LoadIntoW(b.Src2);
                Emit("XORWF", Addr(TmpLo), "W");
                StoreW(b.Dst);
                break;

            case IrBinOp.LShift:
                if (b.Src2 is Constant ls)
                {
                    int lsBits = ls.Value & 7;
                    LoadIntoW(b.Src1);
                    for (int i = 0; i < lsBits; i++)
                    {
                        Emit("MOVWF", Addr(TmpLo));
                        Emit("BCF",   Addr(StatusReg), "0");  // clear carry (C=bit0)
                        Emit("RLF",   Addr(TmpLo), "W");      // rotate left through carry
                    }
                    // Mask out bits that wrapped through the carry rotation.
                    if (lsBits > 0)
                        Emit("ANDLW", $"0x{(0xFF << lsBits) & 0xFF:X2}");
                }
                else
                {
                    // Variable-count left shift using a loop.
                    // TmpLo = src1 (value to shift), Tmp2Lo = count
                    LoadIntoW(b.Src1);
                    Emit("MOVWF", Addr(TmpLo));
                    LoadIntoW(b.Src2);
                    Emit("MOVWF", Addr(Tmp2Lo));
                    string loopL = MakeLabel("L14_LSH_LOOP");
                    string doneL = MakeLabel("L14_LSH_DONE");
                    EmitLabel(loopL);
                    Emit("MOVF",  Addr(Tmp2Lo), "F");           // test counter; sets Z
                    Emit("BTFSC", Addr(StatusReg), $"{StatusZ}"); // Z=1 → done
                    Emit("GOTO",  doneL);
                    Emit("BCF",   Addr(StatusReg), "0");        // clear carry
                    Emit("RLF",   Addr(TmpLo), "F");            // TmpLo <<= 1
                    Emit("DECF",  Addr(Tmp2Lo), "F");           // count--
                    Emit("GOTO",  loopL);
                    EmitLabel(doneL);
                    Emit("MOVF",  Addr(TmpLo), "W");
                }
                StoreW(b.Dst);
                break;

            case IrBinOp.RShift:
                if (b.Src2 is Constant rs)
                {
                    int rsBits = rs.Value & 7;
                    LoadIntoW(b.Src1);
                    for (int i = 0; i < rsBits; i++)
                    {
                        Emit("MOVWF", Addr(TmpLo));
                        Emit("BCF",   Addr(StatusReg), "0");  // clear carry for logical shift
                        Emit("RRF",   Addr(TmpLo), "W");      // rotate right through carry
                    }
                    // Mask out bits that wrapped through carry (carry enters from MSB side).
                    if (rsBits > 0)
                        Emit("ANDLW", $"0x{(0xFF >> rsBits) & 0xFF:X2}");
                }
                else
                {
                    // Variable-count right shift using a loop.
                    LoadIntoW(b.Src1);
                    Emit("MOVWF", Addr(TmpLo));
                    LoadIntoW(b.Src2);
                    Emit("MOVWF", Addr(Tmp2Lo));
                    string loopL = MakeLabel("L14_RSH_LOOP");
                    string doneL = MakeLabel("L14_RSH_DONE");
                    EmitLabel(loopL);
                    Emit("MOVF",  Addr(Tmp2Lo), "F");
                    Emit("BTFSC", Addr(StatusReg), $"{StatusZ}");
                    Emit("GOTO",  doneL);
                    Emit("BCF",   Addr(StatusReg), "0");
                    Emit("RRF",   Addr(TmpLo), "F");
                    Emit("DECF",  Addr(Tmp2Lo), "F");
                    Emit("GOTO",  loopL);
                    EmitLabel(doneL);
                    Emit("MOVF",  Addr(TmpLo), "W");
                }
                StoreW(b.Dst);
                break;

            case IrBinOp.Mul:
                EmitComment("Mul: software multiply not yet implemented on PIC14");
                LoadIntoW(b.Src1);
                StoreW(b.Dst);
                break;

            case IrBinOp.Div:
            case IrBinOp.FloorDiv:
            {
                // Software 8-bit divide: TmpLo=dividend, Tmp2Lo=divisor, CALL __pic14_div8
                // After call: TmpLo = quotient, TmpHi = remainder
                LoadIntoW(b.Src1);
                Emit("MOVWF", Addr(TmpLo));
                LoadIntoW(b.Src2);
                Emit("MOVWF", Addr(Tmp2Lo));
                _needsDiv8 = true;
                Emit("CALL",  "__pic14_div8");
                Emit("MOVF",  Addr(TmpLo), "W");  // quotient in TmpLo
                StoreW(b.Dst);
                break;
            }

            case IrBinOp.Mod:
            {
                LoadIntoW(b.Src1);
                Emit("MOVWF", Addr(TmpLo));
                LoadIntoW(b.Src2);
                Emit("MOVWF", Addr(Tmp2Lo));
                _needsDiv8 = true;
                Emit("CALL",  "__pic14_div8");
                Emit("MOVF",  Addr(TmpHi), "W");  // remainder in TmpHi
                StoreW(b.Dst);
                break;
            }

            case IrBinOp.Equal:
                EmitCompare8(b.Src1, b.Src2);
                EmitBoolFromStatusZ(true, b.Dst);
                break;
            case IrBinOp.NotEqual:
                EmitCompare8(b.Src1, b.Src2);
                EmitBoolFromStatusZ(false, b.Dst);
                break;
            case IrBinOp.LessThan:
            {
                bool signed = GetValType(b.Src1).IsSigned() || GetValType(b.Src2).IsSigned();
                if (signed)
                    EmitSignedBoolLT(b.Src1, b.Src2, b.Dst);
                else
                { EmitCompare8(b.Src1, b.Src2); EmitBoolFromStatusC(false, b.Dst); }
                break;
            }
            case IrBinOp.GreaterEqual:
            {
                bool signed = GetValType(b.Src1).IsSigned() || GetValType(b.Src2).IsSigned();
                if (signed)
                    EmitSignedBoolGE(b.Src1, b.Src2, b.Dst);
                else
                { EmitCompare8(b.Src1, b.Src2); EmitBoolFromStatusC(true, b.Dst); }
                break;
            }
            case IrBinOp.GreaterThan:
            {
                bool signed = GetValType(b.Src1).IsSigned() || GetValType(b.Src2).IsSigned();
                if (signed)
                    EmitSignedBoolGT(b.Src1, b.Src2, b.Dst);
                else
                { EmitCompare8(b.Src1, b.Src2); EmitBoolGT(b.Dst); }
                break;
            }
            case IrBinOp.LessEqual:
            {
                bool signed = GetValType(b.Src1).IsSigned() || GetValType(b.Src2).IsSigned();
                if (signed)
                    EmitSignedBoolLE(b.Src1, b.Src2, b.Dst);
                else
                { EmitCompare8(b.Src1, b.Src2); EmitBoolLE(b.Dst); }
                break;
            }
        }
    }

    // Materialise boolean from Z flag.  `wantSet=true` → 1 when Z=1.
    private void EmitBoolFromStatusZ(bool wantSet, Val dst)
    {
        string trueL = MakeLabel("L14_BOOLZ_T");
        string doneL = MakeLabel("L14_BOOLZ_D");
        if (wantSet)
        {
            Emit("BTFSC", Addr(StatusReg), $"{StatusZ}");  // Z=0 → skip → false path
            Emit("GOTO",  trueL);
            Emit("CLRW");
            Emit("GOTO",  doneL);
            EmitLabel(trueL);
            Emit("MOVLW", "0x01");
        }
        else
        {
            Emit("BTFSS", Addr(StatusReg), $"{StatusZ}");  // Z=1 → skip → false path
            Emit("GOTO",  trueL);
            Emit("CLRW");
            Emit("GOTO",  doneL);
            EmitLabel(trueL);
            Emit("MOVLW", "0x01");
        }
        EmitLabel(doneL);
        StoreW(dst);
    }

    // Materialise boolean from C flag.  `wantSet=true` → 1 when C=1.
    private void EmitBoolFromStatusC(bool wantSet, Val dst)
    {
        string trueL = MakeLabel("L14_BOOLC_T");
        string doneL = MakeLabel("L14_BOOLC_D");
        if (wantSet)
        {
            Emit("BTFSC", Addr(StatusReg), $"{StatusC}");  // C=0 → skip → false path
            Emit("GOTO",  trueL);
            Emit("CLRW");
            Emit("GOTO",  doneL);
            EmitLabel(trueL);
            Emit("MOVLW", "0x01");
        }
        else
        {
            Emit("BTFSS", Addr(StatusReg), $"{StatusC}");  // C=1 → skip → false path
            Emit("GOTO",  trueL);
            Emit("CLRW");
            Emit("GOTO",  doneL);
            EmitLabel(trueL);
            Emit("MOVLW", "0x01");
        }
        EmitLabel(doneL);
        StoreW(dst);
    }

    private void EmitBoolGT(Val dst)
    {
        string falseL = MakeLabel("L14_GT_F");
        string doneL  = MakeLabel("L14_GT_D");
        // Z=1 (equal) → false.
        Emit("BTFSC", Addr(StatusReg), $"{StatusZ}");  // Z=0 → skip
        Emit("GOTO",  falseL);
        // Z=0 here. C=0 (src1<src2) → false.
        Emit("BTFSS", Addr(StatusReg), $"{StatusC}");  // C=1 → skip → false not taken
        Emit("GOTO",  falseL);
        Emit("MOVLW", "0x01");
        Emit("GOTO",  doneL);
        EmitLabel(falseL);
        Emit("CLRW");
        EmitLabel(doneL);
        StoreW(dst);
    }

    private void EmitBoolLE(Val dst)
    {
        string trueL = MakeLabel("L14_LE_T");
        string doneL = MakeLabel("L14_LE_D");
        string skipL = MakeLabel("L14_LE_SK");
        // Z=1 (equal) → true.
        Emit("BTFSC", Addr(StatusReg), $"{StatusZ}");  // Z=0 → skip → check carry
        Emit("GOTO",  trueL);
        // Z=0; C=0 (src1 < src2) → true; C=1 (src1 > src2) → false.
        Emit("BTFSS", Addr(StatusReg), $"{StatusC}");  // C=1 → skip → false
        Emit("GOTO",  trueL);
        Emit("CLRW");
        Emit("GOTO",  doneL);
        EmitLabel(trueL);
        Emit("MOVLW", "0x01");
        EmitLabel(doneL);
        StoreW(dst);
    }

    // -------------------------------------------------------------------------
    // Signed comparison helpers (PIC14 has no OV bit; use sign-bit XOR approach)
    // -------------------------------------------------------------------------
    //
    // Signed LT(src1, src2): true when src1 < src2 (signed)
    // Algorithm (no OV bit):
    //   1. XOR bit7 of src1 and src2; if different signs: src1<src2 iff src1 bit7=1
    //   2. Same signs: result == unsigned comparison from subtraction (C flag)

    private void EmitSignedBoolLT(Val src1, Val src2, Val dst)
    {
        // Load src1 and src2 into TmpLo / Tmp2Lo for bit testing.
        LoadIntoW(src1); Emit("MOVWF", Addr(TmpLo));
        LoadIntoW(src2); Emit("MOVWF", Addr(Tmp2Lo));
        // XOR the two bytes to test sign equality.
        Emit("XORWF", Addr(TmpLo), "W");    // W = src1 XOR src2
        Emit("MOVWF", Addr(TmpHi));         // TmpHi = xored value
        string sameSigns = MakeLabel("L14_SLT_SS");
        string resultTrue = MakeLabel("L14_SLT_T");
        string resultFalse = MakeLabel("L14_SLT_F");
        string done = MakeLabel("L14_SLT_D");
        // If bit7 of xor == 0 → same signs
        Emit("BTFSS", Addr(TmpHi), "7");    // bit7=1 → different signs
        Emit("GOTO",  sameSigns);
        // Different signs: src1 < src2 iff src1 is negative (bit7=1)
        Emit("BTFSC", Addr(TmpLo), "7");    // src1 bit7=0 → skip → src1 positive → not lt
        Emit("GOTO",  resultTrue);           // src1 bit7=1 → src1 negative → lt
        Emit("GOTO",  resultFalse);
        EmitLabel(sameSigns);
        // Same signs: do unsigned comparison (src1 - src2) via carry
        Emit("MOVF",  Addr(Tmp2Lo), "W");
        Emit("SUBWF", Addr(TmpLo), "W");    // W = src1 - src2; C=1 if src1>=src2
        Emit("BTFSC", Addr(StatusReg), $"{StatusC}"); // C=1 → skip → src1 >= src2
        Emit("GOTO",  resultFalse);
        Emit("BTFSC", Addr(StatusReg), $"{StatusZ}"); // Z=1 → equal → not lt
        Emit("GOTO",  resultFalse);
        EmitLabel(resultTrue);
        Emit("MOVLW", "0x01");
        Emit("GOTO",  done);
        EmitLabel(resultFalse);
        Emit("CLRW");
        EmitLabel(done);
        StoreW(dst);
    }

    private void EmitSignedBoolGE(Val src1, Val src2, Val dst)
    {
        EmitSignedBoolLT(src1, src2, dst);
        // GE = NOT LT: flip the 0/1 result
        int addr = AddrOf(dst);
        if (addr >= 0)
        {
            Emit("MOVF",  Addr(addr), "W");
            Emit("XORLW", "0x01");
            Emit("MOVWF", Addr(addr));
        }
        else
        {
            // result already in W for non-addressable dst: just flip W
            LoadIntoW(dst);
            Emit("XORLW", "0x01");
            StoreW(dst);
        }
    }

    private void EmitSignedBoolGT(Val src1, Val src2, Val dst)
    {
        // GT(a,b) = LT(b,a)
        EmitSignedBoolLT(src2, src1, dst);
    }

    private void EmitSignedBoolLE(Val src1, Val src2, Val dst)
    {
        // LE(a,b) = NOT GT(a,b) = NOT LT(b,a)
        EmitSignedBoolLT(src2, src1, dst);
        int addr = AddrOf(dst);
        if (addr >= 0)
        {
            Emit("MOVF",  Addr(addr), "W");
            Emit("XORLW", "0x01");
            Emit("MOVWF", Addr(addr));
        }
        else
        {
            LoadIntoW(dst);
            Emit("XORLW", "0x01");
            StoreW(dst);
        }
    }

    // Signed branch helpers for JumpIf* instructions (PIC14).

    private void BranchIfLessThanSigned(Val src1, Val src2, string target)
    {
        LoadIntoW(src1); Emit("MOVWF", Addr(TmpLo));
        LoadIntoW(src2); Emit("MOVWF", Addr(Tmp2Lo));
        Emit("XORWF", Addr(TmpLo), "W"); Emit("MOVWF", Addr(TmpHi));
        string sameSigns = MakeLabel("L14_JSLT_SS");
        string noJump    = MakeLabel("L14_JSLT_NO");
        Emit("BTFSS", Addr(TmpHi), "7");    // diff signs if bit7=1
        Emit("GOTO",  sameSigns);
        // Different signs: jump iff src1 negative
        Emit("BTFSS", Addr(TmpLo), "7");    // src1 bit7=0 → skip → not lt
        Emit("GOTO",  noJump);
        Emit("GOTO",  target);
        EmitLabel(sameSigns);
        // Same signs: jump if C=0 and Z=0 after subtraction
        Emit("MOVF",  Addr(Tmp2Lo), "W");
        Emit("SUBWF", Addr(TmpLo), "W");
        Emit("BTFSC", Addr(StatusReg), $"{StatusC}"); Emit("GOTO", noJump); // C=1 → >=
        Emit("BTFSC", Addr(StatusReg), $"{StatusZ}"); Emit("GOTO", noJump); // Z=1 → =
        Emit("GOTO",  target);
        EmitLabel(noJump);
    }

    private void BranchIfGreaterEqualSigned(Val src1, Val src2, string target)
    {
        // GE = NOT LT: branch when NOT signed_lt(src1, src2)
        string noJump = MakeLabel("L14_JSGE_NO");
        BranchIfLessThanSigned(src1, src2, noJump);
        Emit("GOTO", target);
        EmitLabel(noJump);
    }

    private void BranchIfGreaterThanSigned(Val src1, Val src2, string target)
    {
        // GT(a,b) = LT(b,a)
        BranchIfLessThanSigned(src2, src1, target);
    }

    private void BranchIfLessOrEqualSigned(Val src1, Val src2, string target)
    {
        // LE(a,b) = NOT GT(a,b) = NOT LT(b,a)
        string noJump = MakeLabel("L14_JSLE_NO");
        BranchIfLessThanSigned(src2, src1, noJump);  // jump (noJump) when src1>src2
        Emit("GOTO", target);
        EmitLabel(noJump);
    }

    // 16-bit binary.
    private void CompileBinary16(Binary b)
    {
        Load16Into(b.Src1, TmpLo, TmpHi);
        Load16Into(b.Src2, Tmp2Lo, Tmp2Hi);

        switch (b.Op)
        {
            case IrBinOp.Add:
                // Low byte: add; propagate carry to high byte BEFORE adding high bytes.
                Emit("MOVF",  Addr(Tmp2Lo), "W");
                Emit("ADDWF", Addr(TmpLo),  "F");   // TmpLo += Tmp2Lo; C = carry from lo
                Emit("BTFSC", Addr(StatusReg), $"{StatusC}");  // C=0 → skip INCF (no carry)
                Emit("INCF",  Addr(TmpHi), "F");    // C=1 → carry from lo into hi
                Emit("MOVF",  Addr(Tmp2Hi), "W");
                Emit("ADDWF", Addr(TmpHi),  "F");   // TmpHi += Tmp2Hi
                break;
            case IrBinOp.Sub:
                // Low byte: subtract; propagate borrow to high byte BEFORE subtracting high bytes.
                // After SUBWF: C=0 means borrow (TmpLo < Tmp2Lo).
                Emit("MOVF",  Addr(Tmp2Lo), "W");
                Emit("SUBWF", Addr(TmpLo),  "F");   // TmpLo -= Tmp2Lo; C=0 if borrow
                Emit("BTFSS", Addr(StatusReg), $"{StatusC}");  // C=1 → skip DECF (no borrow)
                Emit("DECF",  Addr(TmpHi), "F");    // C=0 → borrow → decrement hi
                Emit("MOVF",  Addr(Tmp2Hi), "W");
                Emit("SUBWF", Addr(TmpHi),  "F");   // TmpHi -= Tmp2Hi
                break;
            case IrBinOp.BitAnd:
                Emit("MOVF",  Addr(Tmp2Lo), "W"); Emit("ANDWF", Addr(TmpLo), "F");
                Emit("MOVF",  Addr(Tmp2Hi), "W"); Emit("ANDWF", Addr(TmpHi), "F");
                break;
            case IrBinOp.BitOr:
                Emit("MOVF",  Addr(Tmp2Lo), "W"); Emit("IORWF", Addr(TmpLo), "F");
                Emit("MOVF",  Addr(Tmp2Hi), "W"); Emit("IORWF", Addr(TmpHi), "F");
                break;
            case IrBinOp.BitXor:
                Emit("MOVF",  Addr(Tmp2Lo), "W"); Emit("XORWF", Addr(TmpLo), "F");
                Emit("MOVF",  Addr(Tmp2Hi), "W"); Emit("XORWF", Addr(TmpHi), "F");
                break;
            default:
                EmitComment($"16-bit {b.Op} not implemented in PIC14 backend");
                break;
        }
        Store16From(TmpLo, TmpHi, b.Dst);
    }

    // -------------------------------------------------------------------------
    // Bit operations
    // -------------------------------------------------------------------------

    private void CompileBitSet(BitSet bs)
    {
        int addr = AddrOf(bs.Target);
        string f = addr >= 0 ? Addr(addr) : Addr(TmpLo);
        if (addr < 0) { LoadIntoW(bs.Target); Emit("MOVWF", Addr(TmpLo)); }
        Emit("BSF", f, $"{bs.Bit}");
        if (addr < 0) { Emit("MOVF", Addr(TmpLo), "W"); StoreW(bs.Target); }
    }

    private void CompileBitClear(BitClear bc)
    {
        int addr = AddrOf(bc.Target);
        string f = addr >= 0 ? Addr(addr) : Addr(TmpLo);
        if (addr < 0) { LoadIntoW(bc.Target); Emit("MOVWF", Addr(TmpLo)); }
        Emit("BCF", f, $"{bc.Bit}");
        if (addr < 0) { Emit("MOVF", Addr(TmpLo), "W"); StoreW(bc.Target); }
    }

    private void CompileBitCheck(BitCheck bck)
    {
        int addr = AddrOf(bck.Source);
        string f = addr >= 0 ? Addr(addr) : Addr(TmpLo);
        if (addr < 0) { LoadIntoW(bck.Source); Emit("MOVWF", Addr(TmpLo)); }
        string trueL = MakeLabel("L14_BCHK_T");
        string doneL = MakeLabel("L14_BCHK_D");
        Emit("BTFSC", f, $"{bck.Bit}");  // bit=0 → skip → false
        Emit("GOTO",  trueL);
        Emit("CLRW");
        Emit("GOTO",  doneL);
        EmitLabel(trueL);
        Emit("MOVLW", "0x01");
        EmitLabel(doneL);
        StoreW(bck.Dst);
    }

    private void CompileBitWrite(BitWrite bw)
    {
        int tgtAddr = AddrOf(bw.Target);
        string reg;
        if (tgtAddr >= 0)
        {
            reg = Addr(tgtAddr);
        }
        else
        {
            // Load target into TmpLo so BSF/BCF can act on it.
            LoadIntoW(bw.Target);
            Emit("MOVWF", Addr(TmpLo));
            reg = Addr(TmpLo);
        }
        // Load src into Tmp2Lo to test without disturbing TmpLo.
        LoadIntoW(bw.Src);
        Emit("MOVWF", Addr(Tmp2Lo));
        Emit("MOVF",  Addr(Tmp2Lo), "F");           // test src; sets Z
        string setL  = MakeLabel("L14_BW_SET");
        string doneL = MakeLabel("L14_BW_DONE");
        Emit("BTFSC", Addr(StatusReg), $"{StatusZ}"); // Z=0 (nonzero src) → skip GOTO setL → set
        Emit("GOTO",  setL);
        Emit("BCF", reg, $"{bw.Bit}");
        Emit("GOTO",  doneL);
        EmitLabel(setL);
        Emit("BSF", reg, $"{bw.Bit}");
        EmitLabel(doneL);
        if (tgtAddr < 0)
        {
            Emit("MOVF",  Addr(TmpLo), "W");
            StoreW(bw.Target);
        }
    }

    private void CompileJumpIfBitSet(JumpIfBitSet jbs)
    {
        int addr = AddrOf(jbs.Source);
        string f = addr >= 0 ? Addr(addr) : Addr(TmpLo);
        if (addr < 0) { LoadIntoW(jbs.Source); Emit("MOVWF", Addr(TmpLo)); }
        Emit("BTFSC", f, $"{jbs.Bit}");  // bit=0 → skip GOTO
        Emit("GOTO",  jbs.Target);
    }

    private void CompileJumpIfBitClear(JumpIfBitClear jbc)
    {
        int addr = AddrOf(jbc.Source);
        string f = addr >= 0 ? Addr(addr) : Addr(TmpLo);
        if (addr < 0) { LoadIntoW(jbc.Source); Emit("MOVWF", Addr(TmpLo)); }
        Emit("BTFSS", f, $"{jbc.Bit}");  // bit=1 → skip GOTO
        Emit("GOTO",  jbc.Target);
    }

    // -------------------------------------------------------------------------
    // AugAssign
    // -------------------------------------------------------------------------

    private void CompileAugAssign(AugAssign aa)
    {
        int dstAddr = AddrOf(aa.Target);

        if (Is16Bit(aa.Target))
        {
            Load16Into(aa.Target, TmpLo, TmpHi);
            Load16Into(aa.Operand, Tmp2Lo, Tmp2Hi);
            switch (aa.Op)
            {
                case IrBinOp.Add:
                    Emit("MOVF",  Addr(Tmp2Lo), "W");
                    Emit("ADDWF", Addr(TmpLo), "F");
                    Emit("BTFSC", Addr(StatusReg), $"{StatusC}");  // C=0 → skip INCF
                    Emit("INCF",  Addr(TmpHi), "F");   // carry from lo into hi
                    Emit("MOVF",  Addr(Tmp2Hi), "W");
                    Emit("ADDWF", Addr(TmpHi), "F");
                    break;
                case IrBinOp.Sub:
                    Emit("MOVF",  Addr(Tmp2Lo), "W");
                    Emit("SUBWF", Addr(TmpLo), "F");
                    Emit("BTFSS", Addr(StatusReg), $"{StatusC}");  // C=1 → skip DECF (no borrow)
                    Emit("DECF",  Addr(TmpHi), "F");   // borrow from lo into hi
                    Emit("MOVF",  Addr(Tmp2Hi), "W");
                    Emit("SUBWF", Addr(TmpHi), "F");
                    break;
                case IrBinOp.BitAnd:
                    Emit("MOVF",  Addr(Tmp2Lo), "W"); Emit("ANDWF", Addr(TmpLo), "F");
                    Emit("MOVF",  Addr(Tmp2Hi), "W"); Emit("ANDWF", Addr(TmpHi), "F");
                    break;
                case IrBinOp.BitOr:
                    Emit("MOVF",  Addr(Tmp2Lo), "W"); Emit("IORWF", Addr(TmpLo), "F");
                    Emit("MOVF",  Addr(Tmp2Hi), "W"); Emit("IORWF", Addr(TmpHi), "F");
                    break;
                case IrBinOp.BitXor:
                    Emit("MOVF",  Addr(Tmp2Lo), "W"); Emit("XORWF", Addr(TmpLo), "F");
                    Emit("MOVF",  Addr(Tmp2Hi), "W"); Emit("XORWF", Addr(TmpHi), "F");
                    break;
                default:
                    EmitComment($"16-bit AugAssign {aa.Op} not implemented");
                    break;
            }
            Store16From(TmpLo, TmpHi, aa.Target);
            return;
        }

        if (dstAddr >= 0)
        {
            switch (aa.Op)
            {
                case IrBinOp.Add when aa.Operand is Constant { Value: 1 }:
                    Emit("INCF", Addr(dstAddr), "F"); return;
                case IrBinOp.Sub when aa.Operand is Constant { Value: 1 }:
                    Emit("DECF", Addr(dstAddr), "F"); return;
                case IrBinOp.Add:
                    LoadIntoW(aa.Operand);
                    Emit("ADDWF", Addr(dstAddr), "F"); return;
                case IrBinOp.Sub:
                    LoadIntoW(aa.Operand);
                    Emit("SUBWF", Addr(dstAddr), "F"); return;
                case IrBinOp.BitAnd:
                    LoadIntoW(aa.Operand);
                    Emit("ANDWF", Addr(dstAddr), "F"); return;
                case IrBinOp.BitOr:
                    LoadIntoW(aa.Operand);
                    Emit("IORWF", Addr(dstAddr), "F"); return;
                case IrBinOp.BitXor:
                    LoadIntoW(aa.Operand);
                    Emit("XORWF", Addr(dstAddr), "F"); return;
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
                EmitComment($"AugAssign {aa.Op} not fully implemented on PIC14");
                break;
        }
        StoreW(aa.Target);
    }

    // -------------------------------------------------------------------------
    // Array load / store
    // -------------------------------------------------------------------------

    private void CompileArrayLoad(ArrayLoad al)
    {
        if (!_varAddrs.TryGetValue(al.ArrayName, out int baseAddr))
        {
            EmitComment($"TODO(wip): ArrayLoad: '{al.ArrayName}' not in var map");
            return;
        }
        int elemSize = al.ElemType.SizeOf();

        if (al.Index is Constant c)
        {
            int addr = baseAddr + c.Value * elemSize;
            Emit("MOVF", Addr(addr), "W");
            StoreW(al.Dst);
        }
        else
        {
            EmitComment("ArrayLoad via FSR");
            LoadIntoW(al.Index);
            Emit("MOVWF", Addr(TmpLo));
            Emit("MOVLW", $"0x{baseAddr & 0xFF:X2}");
            Emit("ADDWF", Addr(TmpLo), "W");
            Emit("MOVWF", Addr(FsrReg));
            Emit("MOVF",  Addr(IndfReg), "W");
            StoreW(al.Dst);
        }
    }

    private void CompileArrayStore(ArrayStore ast)
    {
        if (!_varAddrs.TryGetValue(ast.ArrayName, out int baseAddr))
        {
            EmitComment($"TODO(wip): ArrayStore: '{ast.ArrayName}' not in var map");
            return;
        }
        int elemSize = ast.ElemType.SizeOf();

        if (ast.Index is Constant c)
        {
            int addr = baseAddr + c.Value * elemSize;
            LoadIntoW(ast.Src);
            Emit("MOVWF", Addr(addr));
        }
        else
        {
            EmitComment("ArrayStore via FSR");
            LoadIntoW(ast.Index);
            Emit("MOVWF", Addr(TmpLo));
            Emit("MOVLW", $"0x{baseAddr & 0xFF:X2}");
            Emit("ADDWF", Addr(TmpLo), "W");
            Emit("MOVWF", Addr(FsrReg));
            LoadIntoW(ast.Src);
            Emit("MOVWF", Addr(IndfReg));
        }
    }

    // -------------------------------------------------------------------------
    // ArrayLoadFlash (RETLW table lookup)
    // -------------------------------------------------------------------------

    private void CompileArrayLoadFlash(ArrayLoadFlash alf)
    {
        // RETLW table read: put index in W, CALL <table_name>_read.
        // The table is emitted after all functions as a RETLW sequence.
        string readLabel = $"__flash_{alf.ArrayName.Replace('.', '_')}_read";
        LoadIntoW(alf.Index);
        if (_multiPage)
            Emit("PAGESEL", readLabel);
        Emit("CALL", readLabel);
        StoreW(alf.Dst);
    }

    // -------------------------------------------------------------------------
    // Flash data table emission (RETLW sequences)
    // -------------------------------------------------------------------------

    private void EmitFlashTables(ProgramIR program)
    {
        var flashDataInstrs = program.Functions
            .SelectMany(f => f.Body)
            .OfType<FlashData>()
            .GroupBy(fd => fd.Name)
            .Select(g => g.First());

        foreach (var fd in flashDataInstrs)
        {
            EmitRaw("");
            EmitComment($"Flash data table: {fd.Name} ({fd.Bytes.Count} bytes)");
            string safeName = fd.Name.Replace('.', '_');
            string readLabel = $"__flash_{safeName}_read";

            // Emit the read helper: caller sets W = index, CALL readLabel
            // Inside: ADDWF PCL, F; then RETLW array...
            // PCLATH must be set correctly by caller (or via PAGESEL in multipage mode).
            EmitLabel(readLabel);
            Emit("ADDWF", Addr(0x02), "F");    // PCL += W  (0x02 = PCL register on PIC14)
            foreach (int b in fd.Bytes)
                Emit("RETLW", $"0x{b & 0xFF:X2}");
        }
    }

    // -------------------------------------------------------------------------
    // Software divide subroutines
    // -------------------------------------------------------------------------

    // 8-bit unsigned divide: TmpLo=dividend, Tmp2Lo=divisor
    // After call: TmpLo=quotient, TmpHi=remainder. Uses Tmp2Hi as bit counter.
    private void EmitDiv8Subroutine()
    {
        EmitRaw("");
        EmitComment("8-bit software divide: TmpLo=dividend, Tmp2Lo=divisor");
        EmitComment("Result: TmpLo=quotient, TmpHi=remainder. Clobbers Tmp2Hi.");
        EmitLabel("__pic14_div8");
        Emit("CLRF",    Addr(TmpHi));            // remainder = 0
        Emit("MOVLW",   "0x08");
        Emit("MOVWF",   Addr(Tmp2Hi));           // bit counter = 8
        EmitLabel("__pic14_div8_loop");
        // Shift left: carry = MSB(TmpLo), TmpLo <<= 1, TmpHi = TmpHi<<1|carry
        Emit("BCF",     Addr(StatusReg), "0");   // C=0 before shift
        Emit("RLF",     Addr(TmpLo), "F");       // C = MSB(TmpLo), TmpLo <<= 1
        Emit("RLF",     Addr(TmpHi), "F");       // TmpHi = TmpHi<<1 | old MSB(TmpLo)
        // Test: TmpHi - Tmp2Lo; if C=1 (TmpHi >= Tmp2Lo), set quotient bit
        Emit("MOVF",    Addr(Tmp2Lo), "W");
        Emit("SUBWF",   Addr(TmpHi), "W");       // W = TmpHi - Tmp2Lo; C=1 if TmpHi >= Tmp2Lo
        Emit("BTFSC",   Addr(StatusReg), "0");   // C=0 → can't subtract → skip
        Emit("MOVWF",   Addr(TmpHi));            // TmpHi = remainder after subtraction
        Emit("BTFSC",   Addr(StatusReg), "0");   // C=0 → no quotient bit → skip
        Emit("BSF",     Addr(TmpLo), "0");       // set LSB of TmpLo (quotient bit)
        Emit("DECFSZ",  Addr(Tmp2Hi), "F");      // count--; skip if 0
        Emit("GOTO",    "__pic14_div8_loop");
        Emit("RETURN");
    }

    // 16-bit unsigned divide: TmpLo:TmpHi=dividend, Tmp2Lo:Tmp2Hi=divisor
    // After call: TmpLo:TmpHi=quotient, remainder in Tmp2Lo:Tmp2Hi.
    // Uses RetValHi as bit counter scratch.
    private void EmitDiv16Subroutine()
    {
        EmitRaw("");
        EmitComment("16-bit software divide (not yet implemented on PIC14)");
        EmitLabel("__pic14_div16");
        Emit("RETURN");
    }
}
