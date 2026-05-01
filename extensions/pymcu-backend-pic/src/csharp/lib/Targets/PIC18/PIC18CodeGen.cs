/*
 * -----------------------------------------------------------------------------
 * PyMCU Compiler (pymcuc) — PIC18 Backend  [Supported WIP]
 * Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
 *
 * SPDX-License-Identifier: MIT
 *
 * -----------------------------------------------------------------------------
 * STATUS: Supported WIP.  Full feature set is the target for production support.
 * No cycle-accurate PIC18 simulator is integrated into the test suite.
 * -----------------------------------------------------------------------------
 *
 * Architecture notes (PIC18):
 *   - 8-bit data bus; W (WREG) is the accumulator.
 *   - Access Bank: GPRs 0x000-0x07F, SFRs 0xF80-0xFFF.
 *   - 16-bit program counter; 21-bit GOTO / CALL targets.
 *   - Hardware call stack (up to 31 levels).
 *   - STATUS flags: C (bit 0), DC (bit 1), Z (bit 2), OV (bit 3), N (bit 4).
 *   - STATUS register at 0xFD8 (Access Bank).
 *   - BZ/BNZ/BC/BNC/BN/BNN/BOV/BNOV: 8-bit relative branches on STATUS bits.
 *   - ADDWFC/SUBWFB: carry-based multi-byte arithmetic.
 *   - RETFIE FAST: restores W, STATUS, BSR from shadow registers.
 *   - TBLRD, TBLRD+: read program memory via TBLPTR (TBLPTRU:H:L at 0xFF8:7:6).
 *
 * Memory layout used by this backend (all in Access Bank):
 *   0x020-0x06F  User variables (80 bytes max; VarBase = 0x020).
 *   0x070        TMP_LO — 8-bit scratch / low byte of 16-bit temp.
 *   0x071        TMP_HI — high byte of 16-bit temp.
 *   0x072        TMP2_LO — secondary scratch low byte.
 *   0x073        TMP2_HI — secondary scratch high byte.
 *   0x074        RETVAL_HI — high byte of 16-bit return value.
 *   0x075-0x07B  ARG_BASE — parameter-passing area (7 bytes, <= 3 uint16 params).
 *   0x07C-0x07D  ISR FSR0 save area (FSR0L, FSR0H).
 *   0x07E-0x07F  ISR PROD save area (PRODL, PRODH).
 *
 * Calling convention (minimal):
 *   - Arguments passed in ARG_BASE area, caller writes before CALL.
 *   - 8-bit return value in W; 16-bit in W (lo) + RETVAL_HI (hi).
 *   - Caller is responsible for preserving any needed live variables.
 *
 * Remaining limitations:
 *   - 32-bit and float types are not supported.
 *   - No banking support; all variables must fit in the Access Bank (80 bytes).
 *   - Inline assembly constraints (%0/%1) are passed through as-is.
 * -----------------------------------------------------------------------------
 */

using PyMCU.Backend.Analysis;
using PyMCU.Backend.Targets.PIC;
using PyMCU.Common.Models;
using PyMCU.IR;
using IrBinOp = PyMCU.IR.BinaryOp;
using IrUnOp  = PyMCU.IR.UnaryOp;

namespace PyMCU.Backend.Targets.PIC18;

public class PIC18CodeGen(DeviceConfig cfg) : CodeGen
{
    private readonly List<PicAsmLine> _assembly = [];
    private Dictionary<string, int>  _varAddrs  = new();   // variable name → absolute address
    private Dictionary<string, int>  _varSizes  = new();   // variable name → byte size
    private Dictionary<string, int>  _funcArgSizes = new(); // function name → list of param byte sizes
    private int                      _labelCounter;
    private Function?                _currentFunction;

    // Software divide subroutine emission flag.
    private bool _needsDiv8 = false;

    // Access Bank scratch area (above user variable space).
    private const int TmpLo    = 0x070;
    private const int TmpHi    = 0x071;
    private const int Tmp2Lo   = 0x072;
    private const int Tmp2Hi   = 0x073;
    private const int RetValHi = 0x074;  // high byte of a 16-bit return value
    private const int ArgBase  = 0x075;  // parameter-passing area (7 bytes)
    private const int VarBase  = 0x020;  // first user-variable address in Access Bank

    // ISR context save area (in Access Bank, above ArgBase).
    private const int IsrFsr0L = 0x07C;  // FSR0L save
    private const int IsrFsr0H = 0x07D;  // FSR0H save
    private const int IsrProdL = 0x07E;  // PRODL save
    private const int IsrProdH = 0x07F;  // PRODH save

    // -------------------------------------------------------------------------
    // Emit helpers
    // -------------------------------------------------------------------------

    private string MakeLabel(string pfx = ".L") => $"{pfx}_{_labelCounter++}";

    // Instruction with 0–3 operands (operands are comma-separated automatically).
    private void Emit(string m) => _assembly.Add(PicAsmLine.MakeInstruction(m));
    private void Emit(string m, string o1) =>
        _assembly.Add(PicAsmLine.MakeInstruction($"{m}\t{o1}"));
    private void Emit(string m, string o1, string o2) =>
        _assembly.Add(PicAsmLine.MakeInstruction($"{m}\t{o1}, {o2}"));
    private void Emit(string m, string o1, string o2, string o3) =>
        _assembly.Add(PicAsmLine.MakeInstruction($"{m}\t{o1}, {o2}, {o3}"));

    private void EmitLabel(string l)   => _assembly.Add(PicAsmLine.MakeLabel(l));
    private void EmitComment(string c) => _assembly.Add(PicAsmLine.MakeComment(c));
    private void EmitRaw(string t)     => _assembly.Add(PicAsmLine.MakeRaw(t));

    // Format an absolute Access Bank address as a hex literal.
    private static string Addr(int a) => $"0x{a:X3}";

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

    // Resolve the absolute memory address for a Val; returns -1 if unknown.
    private int AddrOf(Val val)
    {
        var name = val switch { Variable v => v.Name, Temporary t => t.Name, _ => "" };
        return !string.IsNullOrEmpty(name) && _varAddrs.TryGetValue(name, out int a) ? a : -1;
    }

    // Return true when the Val occupies 2 bytes.
    private bool Is16Bit(Val val)
    {
        var name = val switch { Variable v => v.Name, Temporary t => t.Name, _ => "" };
        if (!string.IsNullOrEmpty(name) && _varSizes.TryGetValue(name, out int sz)) return sz == 2;
        return Is16(GetValType(val));
    }

    // -------------------------------------------------------------------------
    // Load / Store primitives
    // -------------------------------------------------------------------------

    // Load an 8-bit value into WREG.
    private void LoadIntoW(Val val)
    {
        switch (val)
        {
            case Constant c:
                Emit("MOVLW", $"0x{c.Value & 0xFF:X2}");
                return;
            case MemoryAddress mem:
                Emit("MOVF", Addr(mem.Address), "W", "ACCESS");
                return;
        }
        int addr = AddrOf(val);
        if (addr >= 0)
            Emit("MOVF", Addr(addr), "W", "ACCESS");
        else
            EmitComment($"TODO(wip): unresolved 8-bit load for {val}");
    }

    // Store WREG into an 8-bit destination.
    private void StoreW(Val dst)
    {
        if (dst is Constant) return;
        if (dst is MemoryAddress mem)
        {
            Emit("MOVWF", Addr(mem.Address), "ACCESS");
            return;
        }
        int addr = AddrOf(dst);
        if (addr >= 0)
            Emit("MOVWF", Addr(addr), "ACCESS");
        else
            EmitComment($"TODO(wip): unresolved 8-bit store for {dst}");
    }

    // Load a 16-bit value into the scratch pair (lo, hi).
    private void Load16Into(Val val, int lo, int hi)
    {
        switch (val)
        {
            case Constant c:
                Emit("MOVLW", $"0x{c.Value & 0xFF:X2}");
                Emit("MOVWF", Addr(lo), "ACCESS");
                Emit("MOVLW", $"0x{(c.Value >> 8) & 0xFF:X2}");
                Emit("MOVWF", Addr(hi), "ACCESS");
                return;
            case MemoryAddress mem:
                Emit("MOVFF", Addr(mem.Address),     Addr(lo));
                Emit("MOVFF", Addr(mem.Address + 1), Addr(hi));
                return;
        }
        int addr = AddrOf(val);
        if (addr >= 0)
        {
            Emit("MOVFF", Addr(addr),     Addr(lo));
            Emit("MOVFF", Addr(addr + 1), Addr(hi));
        }
        else EmitComment($"TODO(wip): unresolved 16-bit load for {val}");
    }

    // Store scratch pair (lo, hi) into a 16-bit destination.
    private void Store16From(int lo, int hi, Val dst)
    {
        if (dst is Constant) return;
        if (dst is MemoryAddress mem)
        {
            Emit("MOVFF", Addr(lo), Addr(mem.Address));
            Emit("MOVFF", Addr(hi), Addr(mem.Address + 1));
            return;
        }
        int addr = AddrOf(dst);
        if (addr >= 0)
        {
            Emit("MOVFF", Addr(lo), Addr(addr));
            Emit("MOVFF", Addr(hi), Addr(addr + 1));
        }
        else EmitComment($"TODO(wip): unresolved 16-bit store for {dst}");
    }

    // -------------------------------------------------------------------------
    // Compare: after EmitCompare8(src1, src2)
    //   Z=1  ⟺  src1 == src2
    //   C=1  ⟺  src1 >= src2  (unsigned, no borrow)
    //   C=0  ⟺  src1  < src2
    // Uses:  MOVLW src2; SUBWF src1_addr, W, ACCESS  → W = src1 – src2
    //   or:  MOVF src2_addr, W, ACCESS; SUBWF src1_addr, W, ACCESS
    //   or:  MOVF src2_addr, W, ACCESS; SUBLW src1_const  (W = src1 – src2)
    // -------------------------------------------------------------------------

    private void EmitCompare8(Val src1, Val src2)
    {
        int src1Addr = AddrOf(src1);
        int src2Addr = AddrOf(src2);

        if (src2 is Constant c2)
        {
            // Load src2 into W, then SUBWF src1 → W = src1 - src2.
            Emit("MOVLW", $"0x{c2.Value & 0xFF:X2}");
            if (src1 is Constant c1)
                Emit("SUBLW", $"0x{c1.Value & 0xFF:X2}");  // W = c1 - c2 = src1 - src2 ✓
            else if (src1Addr >= 0)
                Emit("SUBWF", Addr(src1Addr), "W", "ACCESS");
            else
            {
                // Fallback: store c2 to TmpLo, load src1 to W, then subtract correctly.
                // load src1 into TmpLo, load src2 into W, SUBWF TmpLo → W = src1 - src2.
                Emit("MOVWF", Addr(Tmp2Lo), "ACCESS");
                LoadIntoW(src1);
                Emit("MOVWF", Addr(TmpLo), "ACCESS");
                Emit("MOVF",  Addr(Tmp2Lo), "W", "ACCESS");
                Emit("SUBWF", Addr(TmpLo), "W", "ACCESS");  // W = TmpLo - Tmp2Lo = src1 - src2
            }
        }
        else if (src1 is Constant c1v)
        {
            // Load W = src2, then SUBLW src1 → W = src1 - src2 ✓
            if (src2Addr >= 0)
                Emit("MOVF", Addr(src2Addr), "W", "ACCESS");
            else
                LoadIntoW(src2);
            Emit("SUBLW", $"0x{c1v.Value & 0xFF:X2}");
        }
        else if (src1Addr >= 0 && src2Addr >= 0)
        {
            Emit("MOVF",  Addr(src2Addr), "W",        "ACCESS"); // W = src2
            Emit("SUBWF", Addr(src1Addr), "W", "ACCESS");        // W = src1 - src2 ✓
        }
        else
        {
            // Generic fallback using scratch registers.
            Load16Into(src1, TmpLo, TmpHi);
            Load16Into(src2, Tmp2Lo, Tmp2Hi);
            Emit("MOVF",  Addr(Tmp2Lo), "W",        "ACCESS");
            Emit("SUBWF", Addr(TmpLo),  "W", "ACCESS");
        }
    }

    // -------------------------------------------------------------------------
    // Conditional branch helpers (PIC18 relative branches + GOTO for range safety)
    //
    // All use the inverted-skip + GOTO pattern so the GOTO can be 21-bit.
    //
    // After EmitCompare8: C=1 if src1>=src2, Z=1 if equal.
    // -------------------------------------------------------------------------

    // Jump to `target` if Z=1 (src1 == src2).
    private void BranchIfEqual(string target)
    {
        string skip = MakeLabel("L18_NE_SKIP");
        Emit("BNZ", skip);
        Emit("GOTO", target);
        EmitLabel(skip);
    }

    // Jump to `target` if Z=0 (src1 != src2).
    private void BranchIfNotEqual(string target)
    {
        string skip = MakeLabel("L18_EQ_SKIP");
        Emit("BZ", skip);
        Emit("GOTO", target);
        EmitLabel(skip);
    }

    // Jump to `target` if C=0 (src1 < src2, unsigned).
    private void BranchIfLessThan(string target)
    {
        string skip = MakeLabel("L18_GE_SKIP");
        Emit("BC", skip);
        Emit("GOTO", target);
        EmitLabel(skip);
    }

    // Jump to `target` if C=1 (src1 >= src2, unsigned).
    private void BranchIfGreaterEqual(string target)
    {
        string skip = MakeLabel("L18_LT_SKIP");
        Emit("BNC", skip);
        Emit("GOTO", target);
        EmitLabel(skip);
    }

    // Jump to `target` if C=1 AND Z=0 (src1 > src2, unsigned).
    private void BranchIfGreaterThan(string target)
    {
        string skip = MakeLabel("L18_LE_SKIP");
        Emit("BZ",  skip);        // Z=1 (equal) → skip GOTO
        Emit("BNC", skip);        // C=0 (src1<src2) → skip GOTO
        Emit("GOTO", target);
        EmitLabel(skip);
    }

    // Jump to `target` if C=0 OR Z=1 (src1 <= src2, unsigned).
    private void BranchIfLessOrEqual(string target)
    {
        string step2 = MakeLabel("L18_LE_STEP2");
        Emit("BNZ", step2);      // Z=0 → check carry
        Emit("GOTO", target);    // Z=1 (equal) → take jump
        EmitLabel(step2);        // Z=0 here
        string skip = MakeLabel("L18_LE_SKIP");
        Emit("BC", skip);        // C=1 AND Z=0 → src1>src2 → don't jump
        Emit("GOTO", target);    // C=0 AND Z=0 → src1<src2 → take jump
        EmitLabel(skip);
    }

    // -------------------------------------------------------------------------
    // top-level Compile
    // -------------------------------------------------------------------------

    public override void Compile(ProgramIR program, TextWriter output)
    {
        _assembly.Clear();
        _labelCounter = 0;

        // Allocate variable addresses.
        var allocator = new StackAllocator();
        var (offsets, _) = allocator.Allocate(program);
        _varSizes = allocator.VariableSizes;

        _varAddrs.Clear();
        foreach (var (name, off) in offsets)
            _varAddrs[name] = VarBase + off;

        // Build function parameter-size map (for correct call-site argument widths).
        _funcArgSizes.Clear();
        foreach (var func in program.Functions)
        {
            int argOff = ArgBase;
            foreach (var p in func.Params)
            {
                int sz = _varSizes.TryGetValue(p, out int s) ? s : 1;
                _funcArgSizes[p] = sz;  // keyed by param name for the callee prologue
                argOff += sz;
            }
        }

        // --- File header ---
        EmitComment($"Generated by pymcuc for {cfg.Chip} [WIP PIC18 backend]");
        EmitRaw("");
        EmitRaw($"\tPROCESSOR {cfg.Chip.ToUpperInvariant()}");
        EmitRaw("");

        // EQU directives for all variables.
        foreach (var (name, addr) in _varAddrs)
        {
            var safe = name.Replace('.', '_');
            EmitRaw($"{safe}\tEQU\t{Addr(addr)}");
        }
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

        // High-priority ISR vector (0x0008).
        EmitRaw("\tORG 0x0008");
        var isrMap = new SortedDictionary<int, Function>();
        foreach (var func in program.Functions.Where(f => f.IsInterrupt))
        {
            if (!isrMap.TryAdd(func.InterruptVector, func))
                throw new InvalidOperationException(
                    $"[PIC18] Multiple ISRs defined for vector 0x{func.InterruptVector:X4}");
        }
        if (isrMap.Count > 0)
            Emit("GOTO", isrMap.First().Value.Name);
        else
            Emit("RETFIE", "FAST");
        EmitRaw("");

        // Low-priority ISR vector (0x0018) — unused placeholder.
        EmitRaw("\tORG 0x0018");
        Emit("RETFIE", "FAST");
        EmitRaw("");

        EmitRaw("\tCODE");
        EmitRaw("");

        // ISR functions first, then regular functions.
        foreach (var func in program.Functions.Where(f => f.IsInterrupt))
            CompileFunction(func);
        foreach (var func in program.Functions.Where(f => !f.IsInterrupt && (!f.IsInline || f.Name == "main")))
            CompileFunction(func);

        // Emit flash data tables (DB sequences) collected during function compilation.
        EmitFlashTables(program);

        // Emit software divide subroutines if needed.
        if (_needsDiv8) EmitDiv8Subroutine();

        EmitRaw("");
        EmitRaw("\tEND");

        foreach (var line in _assembly)
            output.WriteLine(line.ToString());
    }

    // -------------------------------------------------------------------------
    // ISR context save / restore  (PIC18 FAST shadow registers handle W/STATUS/BSR)
    // -------------------------------------------------------------------------

    public override void EmitContextSave()
    {
        EmitComment("ISR context: W, STATUS, BSR saved automatically to shadow regs by CALL FAST");

        // Conditionally save FSR0 if the ISR uses indirect access or variable array indexing.
        bool savesFsr0 = _currentFunction != null && _currentFunction.Body.Any(i =>
            i is LoadIndirect or StoreIndirect or
            ArrayLoad { Index: not Constant } or
            ArrayStore { Index: not Constant });
        if (savesFsr0)
        {
            EmitComment("ISR uses indirect access: save FSR0");
            Emit("MOVFF", "0xFE9", Addr(IsrFsr0L));   // FSR0L (0xFE9) → IsrFsr0L
            Emit("MOVFF", "0xFEA", Addr(IsrFsr0H));   // FSR0H (0xFEA) → IsrFsr0H
        }

        // Conditionally save PRODL/PRODH if the ISR uses multiply.
        bool savesProd = _currentFunction != null && _currentFunction.Body.Any(i =>
            i is Binary { Op: IrBinOp.Mul });
        if (savesProd)
        {
            EmitComment("ISR uses multiply: save PRODL/PRODH");
            Emit("MOVFF", "0xFF3", Addr(IsrProdL));   // PRODL (0xFF3) → IsrProdL
            Emit("MOVFF", "0xFF4", Addr(IsrProdH));   // PRODH (0xFF4) → IsrProdH
        }
    }

    public override void EmitContextRestore()
    {
        // Restore PROD and FSR0 in reverse order.
        bool savesProd = _currentFunction != null && _currentFunction.Body.Any(i =>
            i is Binary { Op: IrBinOp.Mul });
        if (savesProd)
        {
            EmitComment("ISR: restore PRODL/PRODH");
            Emit("MOVFF", Addr(IsrProdH), "0xFF4");   // → PRODH
            Emit("MOVFF", Addr(IsrProdL), "0xFF3");   // → PRODL
        }

        bool savesFsr0 = _currentFunction != null && _currentFunction.Body.Any(i =>
            i is LoadIndirect or StoreIndirect or
            ArrayLoad { Index: not Constant } or
            ArrayStore { Index: not Constant });
        if (savesFsr0)
        {
            EmitComment("ISR: restore FSR0");
            Emit("MOVFF", Addr(IsrFsr0H), "0xFEA");   // → FSR0H
            Emit("MOVFF", Addr(IsrFsr0L), "0xFE9");   // → FSR0L
        }

        EmitComment("ISR context: W, STATUS, BSR restored automatically by RETFIE FAST");
    }

    public override void EmitInterruptReturn() => Emit("RETFIE", "FAST");

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
            EmitComment("PIC18 startup: no explicit SP init needed (hardware stack)");
        }

        // Copy arguments from ARG_BASE into local variable slots.
        if (!func.IsInterrupt && func.Name != "main" && func.Params.Count > 0)
        {
            int argOff = ArgBase;
            for (int k = 0; k < func.Params.Count; k++)
            {
                var pname = func.Params[k];
                int psz = _varSizes.TryGetValue(pname, out int s) ? s : 1;
                if (_varAddrs.TryGetValue(pname, out int paddr))
                {
                    Emit("MOVFF", Addr(argOff), Addr(paddr));
                    if (psz == 2) Emit("MOVFF", Addr(argOff + 1), Addr(paddr + 1));
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
                Emit("RETFIE", "FAST");
                emittedReturn = true;
                continue;
            }
            CompileInstruction(instr);
        }

        if (func.IsInterrupt && !emittedReturn)
        {
            EmitContextRestore();
            Emit("RETFIE", "FAST");
        }
    }

    // -------------------------------------------------------------------------
    // Instruction dispatch
    // -------------------------------------------------------------------------

    private void CompileInstruction(Instruction instr)
    {
        switch (instr)
        {
            case Return r:          CompileReturn(r);         break;
            case Jump j:            Emit("GOTO", j.Target);   break;
            case JumpIfZero jz:     CompileJumpIfZero(jz);    break;
            case JumpIfNotZero jnz: CompileJumpIfNotZero(jnz);break;
            case Label l:           EmitLabel(l.Name);        break;
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
                EmitCompare8(jlt.Src1, jlt.Src2);
                bool isSigned = GetValType(jlt.Src1).IsSigned() || GetValType(jlt.Src2).IsSigned();
                if (isSigned) BranchIfLessThanSigned(jlt.Target);
                else          BranchIfLessThan(jlt.Target);
                break;
            }
            case JumpIfLessOrEqual jle:
            {
                EmitCompare8(jle.Src1, jle.Src2);
                bool isSigned = GetValType(jle.Src1).IsSigned() || GetValType(jle.Src2).IsSigned();
                if (isSigned) BranchIfLessOrEqualSigned(jle.Target);
                else          BranchIfLessOrEqual(jle.Target);
                break;
            }
            case JumpIfGreaterThan jgt:
            {
                EmitCompare8(jgt.Src1, jgt.Src2);
                bool isSigned = GetValType(jgt.Src1).IsSigned() || GetValType(jgt.Src2).IsSigned();
                if (isSigned) BranchIfGreaterThanSigned(jgt.Target);
                else          BranchIfGreaterThan(jgt.Target);
                break;
            }
            case JumpIfGreaterOrEqual jge:
            {
                EmitCompare8(jge.Src1, jge.Src2);
                bool isSigned = GetValType(jge.Src1).IsSigned() || GetValType(jge.Src2).IsSigned();
                if (isSigned) BranchIfGreaterEqualSigned(jge.Target);
                else          BranchIfGreaterEqual(jge.Target);
                break;
            }
            case Call c:            CompileCall(c);            break;
            case Copy cp:           CompileCopy(cp);           break;
            case LoadIndirect li:   CompileLoadIndirect(li);   break;
            case StoreIndirect si:  CompileStoreIndirect(si);  break;
            case Unary u:           CompileUnary(u);           break;
            case Binary b:          CompileBinary(b);          break;
            case BitSet bs:         CompileBitSet(bs);         break;
            case BitClear bc:       CompileBitClear(bc);       break;
            case BitCheck bck:      CompileBitCheck(bck);      break;
            case BitWrite bw:       CompileBitWrite(bw);       break;
            case JumpIfBitSet jbs:  CompileJumpIfBitSet(jbs);  break;
            case JumpIfBitClear jbc:CompileJumpIfBitClear(jbc);break;
            case AugAssign aa:      CompileAugAssign(aa);      break;
            case InlineAsm asm2:    EmitRaw(asm2.Code);        break;
            case ArrayLoad al:      CompileArrayLoad(al);      break;
            case ArrayStore ast:    CompileArrayStore(ast);    break;
            case RoData fd:
                // Flash data table will be emitted after all functions as DB directives.
                break;
            case ArrayLoadRo alf:
                CompileArrayLoadRo(alf);
                break;
            case FloatBinary:
                throw new NotSupportedException("Float operations are not supported on PIC18.");
            case Widen w:
                break; // no-op: handled implicitly by register width
            case Narrow n:
                break; // no-op: handled implicitly by register width
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
                Emit("MOVF",  Addr(TmpLo), "W", "ACCESS");           // W = lo byte
                Emit("MOVFF", Addr(TmpHi), Addr(RetValHi));           // retval_hi = hi byte
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
            Emit("MOVF",  Addr(TmpLo), "W", "ACCESS");
            Emit("IORWF", Addr(TmpHi), "W", "ACCESS");  // W = lo | hi; Z=1 if both zero
        }
        else
        {
            LoadIntoW(jz.Condition);
        }
        // W is set to the value; Z=1 if it was zero.
        string skip = MakeLabel("L18_JZ_SKIP");
        Emit("BNZ", skip);
        Emit("GOTO", jz.Target);
        EmitLabel(skip);
    }

    private void CompileJumpIfNotZero(JumpIfNotZero jnz)
    {
        if (Is16Bit(jnz.Condition))
        {
            Load16Into(jnz.Condition, TmpLo, TmpHi);
            Emit("MOVF",  Addr(TmpLo), "W", "ACCESS");
            Emit("IORWF", Addr(TmpHi), "W", "ACCESS");
        }
        else
        {
            LoadIntoW(jnz.Condition);
        }
        string skip = MakeLabel("L18_JNZ_SKIP");
        Emit("BZ", skip);
        Emit("GOTO", jnz.Target);
        EmitLabel(skip);
    }

    // -------------------------------------------------------------------------
    // Call
    // -------------------------------------------------------------------------

    private void CompileCall(Call call)
    {
        // Place arguments into the ARG_BASE parameter area.
        int argOff = ArgBase;
        foreach (var arg in call.Args)
        {
            if (Is16Bit(arg))
            {
                Load16Into(arg, TmpLo, TmpHi);
                Emit("MOVFF", Addr(TmpLo), Addr(argOff));
                Emit("MOVFF", Addr(TmpHi), Addr(argOff + 1));
                argOff += 2;
            }
            else
            {
                LoadIntoW(arg);
                Emit("MOVWF", Addr(argOff), "ACCESS");
                argOff += 1;
            }
        }

        Emit("CALL", call.FunctionName);

        // Pick up return value.
        if (call.Dst is not NoneVal)
        {
            if (Is16Bit(call.Dst))
            {
                Emit("MOVWF", Addr(TmpLo), "ACCESS");   // W holds lo byte
                Emit("MOVFF", Addr(RetValHi), Addr(TmpHi));
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
    // Indirect load / store  (via FSR0 for 8-bit pointer)
    // -------------------------------------------------------------------------

    private void CompileLoadIndirect(LoadIndirect li)
    {
        // Load pointer address into FSR0 (16-bit SFR at 0xFEA:0xFE9).
        if (Is16Bit(li.SrcPtr))
        {
            Load16Into(li.SrcPtr, TmpLo, TmpHi);
            Emit("MOVFF", Addr(TmpLo), "0xFE9");   // FSR0L
            Emit("MOVFF", Addr(TmpHi), "0xFEA");   // FSR0H
        }
        else
        {
            LoadIntoW(li.SrcPtr);
            Emit("MOVWF", "0xFE9", "ACCESS");  // FSR0L
            Emit("CLRF",  "0xFEA", "ACCESS");  // FSR0H = 0 (assume data space)
        }

        if (Is16Bit(li.Dst))
        {
            Emit("MOVF",  "0xFEF", "W", "ACCESS");  // INDF0 (lo)
            Emit("MOVWF", Addr(TmpLo), "ACCESS");
            Emit("INFSNZ", "0xFE9", "F", "ACCESS"); // FSR0L++
            Emit("INCF",   "0xFEA", "F", "ACCESS"); // FSR0H++ if carry
            Emit("MOVF",  "0xFEF", "W", "ACCESS");  // INDF0 (hi)
            Emit("MOVWF", Addr(TmpHi), "ACCESS");
            Store16From(TmpLo, TmpHi, li.Dst);
        }
        else
        {
            Emit("MOVF", "0xFEF", "W", "ACCESS");  // INDF0 → W
            StoreW(li.Dst);
        }
    }

    private void CompileStoreIndirect(StoreIndirect si)
    {
        if (Is16Bit(si.DstPtr))
        {
            Load16Into(si.DstPtr, TmpLo, TmpHi);
            Emit("MOVFF", Addr(TmpLo), "0xFE9");
            Emit("MOVFF", Addr(TmpHi), "0xFEA");
        }
        else
        {
            LoadIntoW(si.DstPtr);
            Emit("MOVWF", "0xFE9", "ACCESS");
            Emit("CLRF",  "0xFEA", "ACCESS");
        }

        if (Is16Bit(si.Src))
        {
            Load16Into(si.Src, TmpLo, TmpHi);
            Emit("MOVFF", Addr(TmpLo), "0xFEF");   // INDF0 = lo
            Emit("INCF",  "0xFE9", "F", "ACCESS"); // FSR0L++
            Emit("MOVFF", Addr(TmpHi), "0xFEF");   // INDF0 = hi
        }
        else
        {
            LoadIntoW(si.Src);
            Emit("MOVWF", "0xFEF", "ACCESS");  // INDF0 = W
        }
    }

    // -------------------------------------------------------------------------
    // Unary operations
    // -------------------------------------------------------------------------

    private void CompileUnary(Unary u)
    {
        DataType type = GetValType(u.Dst);
        bool is16 = Is16(type);
        int dstAddr = AddrOf(u.Dst);

        if (is16)
        {
            Load16Into(u.Src, TmpLo, TmpHi);
            switch (u.Op)
            {
                case IrUnOp.Neg:
                    // Two's complement negation: negate lo, then complement hi + carry.
                    // NEGF lo: lo = -lo_orig. C=1 if lo_orig==0 (no borrow); C=0 otherwise.
                    // COMF hi: hi = ~hi_orig.
                    // BTFSC STATUS.C (skip if C=0): INCF hi only when C=1 (lo_orig was 0).
                    Emit("NEGF", Addr(TmpLo), "ACCESS");
                    Emit("COMF", Addr(TmpHi), "F", "ACCESS");
                    Emit("BTFSC", "0xFD8", "0", "ACCESS");  // STATUS.C: skip if C=0 (no carry)
                    Emit("INCF",  Addr(TmpHi), "F", "ACCESS");
                    break;
                case IrUnOp.BitNot:
                    Emit("COMF", Addr(TmpLo), "F", "ACCESS");
                    Emit("COMF", Addr(TmpHi), "F", "ACCESS");
                    break;
                case IrUnOp.Not:
                    // Logical NOT: result = (lo | hi) == 0 ? 1 : 0
                    Emit("MOVF",  Addr(TmpLo), "W", "ACCESS");
                    Emit("IORWF", Addr(TmpHi), "W", "ACCESS");
                    string notTrue16 = MakeLabel("L18_NOT16_TRUE");
                    string notDone16 = MakeLabel("L18_NOT16_DONE");
                    string notSkip16 = MakeLabel("L18_NOT16_SKIP");
                    Emit("BNZ", notSkip16);
                    Emit("MOVLW", "0x01");
                    Emit("GOTO",  notDone16);
                    EmitLabel(notSkip16);
                    Emit("CLRW");
                    EmitLabel(notDone16);
                    Emit("MOVWF", Addr(TmpLo), "ACCESS");
                    Emit("CLRF",  Addr(TmpHi), "ACCESS");
                    break;
            }
            Store16From(TmpLo, TmpHi, u.Dst);
            return;
        }

        LoadIntoW(u.Src);
        switch (u.Op)
        {
            case IrUnOp.Neg:
                Emit("MOVWF", Addr(TmpLo), "ACCESS");
                Emit("NEGF",  Addr(TmpLo), "ACCESS");  // NEGF: f = -f
                Emit("MOVF",  Addr(TmpLo), "W", "ACCESS");
                break;
            case IrUnOp.BitNot:
                Emit("MOVWF", Addr(TmpLo), "ACCESS");
                Emit("COMF",  Addr(TmpLo), "W", "ACCESS");  // W = ~f
                break;
            case IrUnOp.Not:
            {
                string trueL  = MakeLabel("L18_NOT_TRUE");
                string doneL  = MakeLabel("L18_NOT_DONE");
                string skipL  = MakeLabel("L18_NOT_SKIP");
                Emit("MOVWF", Addr(TmpLo), "ACCESS");
                Emit("MOVF",  Addr(TmpLo), "F", "ACCESS");  // updates Z
                Emit("BNZ",   skipL);
                Emit("MOVLW", "0x01");
                Emit("GOTO",  doneL);
                EmitLabel(skipL);
                Emit("CLRW");
                EmitLabel(doneL);
                break;
            }
        }
        StoreW(u.Dst);
    }

    // -------------------------------------------------------------------------
    // Binary operations
    // -------------------------------------------------------------------------

    private void CompileBinary(Binary b)
    {
        DataType type = GetValType(b.Dst);
        bool is16 = Is16(type);

        if (is16)
        {
            CompileBinary16(b);
            return;
        }

        // 8-bit path.
        int dstAddr = AddrOf(b.Dst);

        switch (b.Op)
        {
            case IrBinOp.Add:
                if (b.Src2 is Constant addK && AddrOf(b.Src1) is int addA and >= 0 && dstAddr >= 0)
                {
                    Emit("MOVLW", $"0x{addK.Value & 0xFF:X2}");
                    Emit("ADDWF", Addr(addA), "W", "ACCESS");
                    StoreW(b.Dst);
                }
                else
                {
                    LoadIntoW(b.Src1);
                    Emit("MOVWF", Addr(TmpLo), "ACCESS");
                    LoadIntoW(b.Src2);
                    Emit("ADDWF", Addr(TmpLo), "W", "ACCESS");  // W = TmpLo + W
                    StoreW(b.Dst);
                }
                break;

            case IrBinOp.Sub:
                // W = src1 - src2: load src2, SUBWF src1 → W = src1 - src2.
                if (b.Src2 is Constant subK && AddrOf(b.Src1) is int subA and >= 0)
                {
                    Emit("MOVLW", $"0x{subK.Value & 0xFF:X2}");
                    Emit("SUBWF", Addr(subA), "W", "ACCESS");
                    StoreW(b.Dst);
                }
                else if (b.Src1 is Constant subK1)
                {
                    LoadIntoW(b.Src2);
                    Emit("SUBLW", $"0x{subK1.Value & 0xFF:X2}");
                    StoreW(b.Dst);
                }
                else
                {
                    LoadIntoW(b.Src2);
                    int a = AddrOf(b.Src1);
                    if (a >= 0)
                        Emit("SUBWF", Addr(a), "W", "ACCESS");
                    else
                    {
                        Emit("MOVWF", Addr(TmpLo), "ACCESS");
                        LoadIntoW(b.Src1);
                        Emit("SUBWF", Addr(TmpLo), "W", "ACCESS"); // approx
                    }
                    StoreW(b.Dst);
                }
                break;

            case IrBinOp.BitAnd:
                LoadIntoW(b.Src1);
                Emit("MOVWF", Addr(TmpLo), "ACCESS");
                LoadIntoW(b.Src2);
                Emit("ANDWF", Addr(TmpLo), "W", "ACCESS");
                StoreW(b.Dst);
                break;

            case IrBinOp.BitOr:
                LoadIntoW(b.Src1);
                Emit("MOVWF", Addr(TmpLo), "ACCESS");
                LoadIntoW(b.Src2);
                Emit("IORWF", Addr(TmpLo), "W", "ACCESS");
                StoreW(b.Dst);
                break;

            case IrBinOp.BitXor:
                LoadIntoW(b.Src1);
                Emit("MOVWF", Addr(TmpLo), "ACCESS");
                LoadIntoW(b.Src2);
                Emit("XORWF", Addr(TmpLo), "W", "ACCESS");
                StoreW(b.Dst);
                break;

            case IrBinOp.LShift:
                if (b.Src2 is Constant shiftL)
                {
                    int lsBits = shiftL.Value & 7;
                    LoadIntoW(b.Src1);
                    for (int i = 0; i < lsBits; i++)
                    {
                        Emit("MOVWF", Addr(TmpLo), "ACCESS");
                        Emit("RLNCF", Addr(TmpLo), "W", "ACCESS");  // rotate left no-carry
                    }
                    // Mask out bits that wrapped through the rotation (RLNCF uses circular shift,
                    // so the LSBs receive bits from the MSB of the previous step).
                    if (lsBits > 0)
                        Emit("ANDLW", $"0x{(0xFF << lsBits) & 0xFF:X2}");
                    StoreW(b.Dst);
                }
                else
                {
                    // Variable-count left shift loop: TmpLo=value, Tmp2Lo=count
                    LoadIntoW(b.Src1);
                    Emit("MOVWF", Addr(TmpLo), "ACCESS");
                    LoadIntoW(b.Src2);
                    Emit("MOVWF", Addr(Tmp2Lo), "ACCESS");
                    string loopL = MakeLabel("L18_LSH_LOOP");
                    string doneL = MakeLabel("L18_LSH_DONE");
                    EmitLabel(loopL);
                    Emit("TSTFSZ", Addr(Tmp2Lo), "ACCESS");   // skip if Tmp2Lo=0
                    Emit("BRA",    doneL + "_SKIP");          // non-zero → don't go to done
                    Emit("BRA",    doneL);
                    EmitLabel(doneL + "_SKIP");
                    // RLNCF is circular; clear the carry-in bit by masking after shift.
                    Emit("RLNCF", Addr(TmpLo), "F", "ACCESS");
                    // Mask out the wrapped MSB→LSB bit after each rotation.
                    Emit("BCF", Addr(TmpLo), "0", "ACCESS");  // clear bit0 (came from MSB)
                    Emit("DECF", Addr(Tmp2Lo), "F", "ACCESS");
                    Emit("BRA",  loopL);
                    EmitLabel(doneL);
                    Emit("MOVF", Addr(TmpLo), "W", "ACCESS");
                    StoreW(b.Dst);
                }
                break;

            case IrBinOp.RShift:
                if (b.Src2 is Constant shiftR)
                {
                    int rsBits = shiftR.Value & 7;
                    LoadIntoW(b.Src1);
                    for (int i = 0; i < rsBits; i++)
                    {
                        Emit("MOVWF", Addr(TmpLo), "ACCESS");
                        bool asr = type.IsSigned();
                        if (asr)
                        {
                            // Arithmetic right shift: propagate sign bit via carry.
                            Emit("BCF",   "0xFD8", "0", "ACCESS");     // STATUS.C = 0
                            Emit("BTFSC", Addr(TmpLo), "7", "ACCESS"); // if bit7=0, skip
                            Emit("BSF",   "0xFD8", "0", "ACCESS");     // STATUS.C = sign bit
                            Emit("RRCF",  Addr(TmpLo), "W", "ACCESS"); // shift right, MSB = C
                        }
                        else
                        {
                            Emit("RRNCF", Addr(TmpLo), "W", "ACCESS");  // logical shift right no-carry
                        }
                    }
                    // Mask out MSB bits that wrapped through RRNCF (circular) for logical shift.
                    if (rsBits > 0 && !type.IsSigned())
                        Emit("ANDLW", $"0x{(0xFF >> rsBits) & 0xFF:X2}");
                    StoreW(b.Dst);
                }
                else
                {
                    // Variable-count right shift loop: TmpLo=value, Tmp2Lo=count
                    LoadIntoW(b.Src1);
                    Emit("MOVWF", Addr(TmpLo), "ACCESS");
                    LoadIntoW(b.Src2);
                    Emit("MOVWF", Addr(Tmp2Lo), "ACCESS");
                    string loopLR = MakeLabel("L18_RSH_LOOP");
                    string doneLR = MakeLabel("L18_RSH_DONE");
                    EmitLabel(loopLR);
                    Emit("TSTFSZ", Addr(Tmp2Lo), "ACCESS");
                    Emit("BRA",    doneLR + "_SKIP");
                    Emit("BRA",    doneLR);
                    EmitLabel(doneLR + "_SKIP");
                    if (type.IsSigned())
                    {
                        // Arithmetic: propagate sign bit.
                        Emit("BCF",   "0xFD8", "0", "ACCESS");
                        Emit("BTFSC", Addr(TmpLo), "7", "ACCESS");
                        Emit("BSF",   "0xFD8", "0", "ACCESS");
                        Emit("RRCF",  Addr(TmpLo), "F", "ACCESS");
                    }
                    else
                    {
                        Emit("RRNCF", Addr(TmpLo), "F", "ACCESS");
                        Emit("BCF",   Addr(TmpLo), "7", "ACCESS");  // clear wrapped-in MSB
                    }
                    Emit("DECF", Addr(Tmp2Lo), "F", "ACCESS");
                    Emit("BRA",  loopLR);
                    EmitLabel(doneLR);
                    Emit("MOVF", Addr(TmpLo), "W", "ACCESS");
                    StoreW(b.Dst);
                }
                break;

            case IrBinOp.Mul:
                // PIC18 has MULWF/MULLW: 8×8 unsigned multiply, result in PRODH:PRODL (0xFF4:0xFF3).
                LoadIntoW(b.Src1);
                {
                    int mulA = AddrOf(b.Src2);
                    if (b.Src2 is Constant mulK)
                        Emit("MULLW", $"0x{mulK.Value & 0xFF:X2}");
                    else if (mulA >= 0)
                        Emit("MULWF", Addr(mulA), "ACCESS");
                    else
                    {
                        Emit("MOVWF", Addr(TmpLo), "ACCESS");
                        LoadIntoW(b.Src2);
                        Emit("MULWF", Addr(TmpLo), "ACCESS");
                    }
                }
                Emit("MOVF", "0xFF3", "W", "ACCESS");  // PRODL (lo byte of 8×8 result)
                StoreW(b.Dst);
                break;

            case IrBinOp.Div:
            case IrBinOp.FloorDiv:
            {
                // Software 8-bit divide: TmpLo=dividend, Tmp2Lo=divisor, CALL __pic18_div8
                // After call: TmpLo=quotient, TmpHi=remainder
                LoadIntoW(b.Src1);
                Emit("MOVWF", Addr(TmpLo),  "ACCESS");
                LoadIntoW(b.Src2);
                Emit("MOVWF", Addr(Tmp2Lo), "ACCESS");
                _needsDiv8 = true;
                Emit("CALL", "__pic18_div8");
                Emit("MOVF", Addr(TmpLo), "W", "ACCESS");  // quotient
                StoreW(b.Dst);
                break;
            }

            case IrBinOp.Mod:
            {
                LoadIntoW(b.Src1);
                Emit("MOVWF", Addr(TmpLo),  "ACCESS");
                LoadIntoW(b.Src2);
                Emit("MOVWF", Addr(Tmp2Lo), "ACCESS");
                _needsDiv8 = true;
                Emit("CALL", "__pic18_div8");
                Emit("MOVF", Addr(TmpHi), "W", "ACCESS");  // remainder
                StoreW(b.Dst);
                break;
            }

            case IrBinOp.Equal:
                EmitCompare8(b.Src1, b.Src2);
                EmitBoolFromZ(true, b.Dst);
                break;
            case IrBinOp.NotEqual:
                EmitCompare8(b.Src1, b.Src2);
                EmitBoolFromZ(false, b.Dst);
                break;
            case IrBinOp.LessThan:
            {
                EmitCompare8(b.Src1, b.Src2);
                bool isSigned = GetValType(b.Src1).IsSigned() || GetValType(b.Src2).IsSigned();
                if (isSigned) EmitSignedBoolLT(b.Dst);
                else          EmitBoolFromC(false, b.Dst);
                break;
            }
            case IrBinOp.GreaterEqual:
            {
                EmitCompare8(b.Src1, b.Src2);
                bool isSigned = GetValType(b.Src1).IsSigned() || GetValType(b.Src2).IsSigned();
                if (isSigned) EmitSignedBoolGE(b.Dst);
                else          EmitBoolFromC(true, b.Dst);
                break;
            }
            case IrBinOp.GreaterThan:
            {
                EmitCompare8(b.Src1, b.Src2);
                bool isSigned = GetValType(b.Src1).IsSigned() || GetValType(b.Src2).IsSigned();
                if (isSigned) EmitSignedBoolGT(b.Dst);
                else          EmitBoolGT(b.Dst);
                break;
            }
            case IrBinOp.LessEqual:
            {
                EmitCompare8(b.Src1, b.Src2);
                bool isSigned = GetValType(b.Src1).IsSigned() || GetValType(b.Src2).IsSigned();
                if (isSigned) EmitSignedBoolLE(b.Dst);
                else          EmitBoolLE(b.Dst);
                break;
            }
        }
    }

    // Materialise a boolean (0/1) from Z=1 into dst.  `wantSet` true → 1 when Z=1.
    private void EmitBoolFromZ(bool wantSet, Val dst)
    {
        string trueL = MakeLabel("L18_BOOLZ_T");
        string doneL = MakeLabel("L18_BOOLZ_D");
        if (wantSet)
        {
            Emit("BNZ", trueL);    // Z=0 → false
            Emit("MOVLW", "0x01"); // Z=1 → true
            Emit("GOTO",  doneL);
            EmitLabel(trueL);
            Emit("CLRW");
        }
        else
        {
            Emit("BZ", trueL);
            Emit("MOVLW", "0x01");
            Emit("GOTO",  doneL);
            EmitLabel(trueL);
            Emit("CLRW");
        }
        EmitLabel(doneL);
        StoreW(dst);
    }

    // Materialise boolean from C flag; `wantSet` true → 1 when C=1.
    private void EmitBoolFromC(bool wantSet, Val dst)
    {
        string trueL = MakeLabel("L18_BOOLC_T");
        string doneL = MakeLabel("L18_BOOLC_D");
        if (wantSet)
        {
            Emit("BNC", trueL);
            Emit("MOVLW", "0x01");
            Emit("GOTO",  doneL);
            EmitLabel(trueL);
            Emit("CLRW");
        }
        else
        {
            Emit("BC", trueL);
            Emit("MOVLW", "0x01");
            Emit("GOTO",  doneL);
            EmitLabel(trueL);
            Emit("CLRW");
        }
        EmitLabel(doneL);
        StoreW(dst);
    }

    // Materialise boolean 1 when C=1 AND Z=0 (greater-than), else 0.
    private void EmitBoolGT(Val dst)
    {
        string falseL = MakeLabel("L18_BOOLGT_F");
        string doneL  = MakeLabel("L18_BOOLGT_D");
        Emit("BZ",  falseL);     // equal → false
        Emit("BNC", falseL);     // src1<src2 → false
        Emit("MOVLW", "0x01");
        Emit("GOTO",  doneL);
        EmitLabel(falseL);
        Emit("CLRW");
        EmitLabel(doneL);
        StoreW(dst);
    }

    // Materialise boolean 1 when C=0 OR Z=1 (less-or-equal), else 0.
    private void EmitBoolLE(Val dst)
    {
        string trueL = MakeLabel("L18_BOOLLE_T");
        string doneL = MakeLabel("L18_BOOLLE_D");
        string skipL = MakeLabel("L18_BOOLLE_S");
        Emit("BNZ", skipL);       // Z=0 → check carry
        Emit("MOVLW", "0x01");    // Z=1 → result = true
        Emit("GOTO",  doneL);
        EmitLabel(skipL);
        Emit("BC", trueL);        // C=1 AND Z=0 → src1>src2 → false; but wait, we want BNC...
        Emit("MOVLW", "0x01");    // C=0 AND Z=0 → src1<src2 → true
        Emit("GOTO",  doneL);
        EmitLabel(trueL);
        Emit("CLRW");             // C=1 AND Z=0 → src1>src2 → false
        EmitLabel(doneL);
        StoreW(dst);
    }

    // 16-bit binary operations using Access Bank scratch area.
    private void CompileBinary16(Binary b)
    {
        Load16Into(b.Src1, TmpLo, TmpHi);
        Load16Into(b.Src2, Tmp2Lo, Tmp2Hi);

        switch (b.Op)
        {
            case IrBinOp.Add:
                // W = src1_lo + src2_lo; carry into high byte via ADDWFC.
                Emit("MOVF",   Addr(Tmp2Lo), "W", "ACCESS");
                Emit("ADDWF",  Addr(TmpLo),  "F", "ACCESS");  // TmpLo += Tmp2Lo; C = carry
                Emit("MOVF",   Addr(Tmp2Hi), "W", "ACCESS");
                Emit("ADDWFC", Addr(TmpHi),  "F", "ACCESS");  // TmpHi += Tmp2Hi + C
                break;
            case IrBinOp.Sub:
                // TmpLo -= Tmp2Lo; then TmpHi -= Tmp2Hi with borrow (SUBWFB).
                Emit("MOVF",   Addr(Tmp2Lo), "W", "ACCESS");
                Emit("SUBWF",  Addr(TmpLo),  "F", "ACCESS");  // TmpLo = TmpLo - Tmp2Lo; C=borrow
                Emit("MOVF",   Addr(Tmp2Hi), "W", "ACCESS");
                Emit("SUBWFB", Addr(TmpHi),  "F", "ACCESS");  // TmpHi = TmpHi - Tmp2Hi - ~C
                break;
            case IrBinOp.BitAnd:
                Emit("MOVF",  Addr(Tmp2Lo), "W", "ACCESS");
                Emit("ANDWF", Addr(TmpLo),  "F", "ACCESS");
                Emit("MOVF",  Addr(Tmp2Hi), "W", "ACCESS");
                Emit("ANDWF", Addr(TmpHi),  "F", "ACCESS");
                break;
            case IrBinOp.BitOr:
                Emit("MOVF",  Addr(Tmp2Lo), "W", "ACCESS");
                Emit("IORWF", Addr(TmpLo),  "F", "ACCESS");
                Emit("MOVF",  Addr(Tmp2Hi), "W", "ACCESS");
                Emit("IORWF", Addr(TmpHi),  "F", "ACCESS");
                break;
            case IrBinOp.BitXor:
                Emit("MOVF",  Addr(Tmp2Lo), "W", "ACCESS");
                Emit("XORWF", Addr(TmpLo),  "F", "ACCESS");
                Emit("MOVF",  Addr(Tmp2Hi), "W", "ACCESS");
                Emit("XORWF", Addr(TmpHi),  "F", "ACCESS");
                break;
            default:
                EmitComment($"16-bit {b.Op} not yet implemented in PIC18 backend");
                break;
        }
        Store16From(TmpLo, TmpHi, b.Dst);
    }

    // -------------------------------------------------------------------------
    // Bit operations
    // -------------------------------------------------------------------------

    private void CompileBitSet(BitSet bs)
    {
        if (bs.Target is MemoryAddress mem)
        {
            Emit("BSF", Addr(mem.Address), $"{bs.Bit}", "ACCESS");
            return;
        }
        int addr = AddrOf(bs.Target);
        if (addr >= 0)
        {
            Emit("BSF", Addr(addr), $"{bs.Bit}", "ACCESS");
        }
        else
        {
            LoadIntoW(bs.Target);
            Emit("MOVWF", Addr(TmpLo), "ACCESS");
            Emit("BSF",   Addr(TmpLo), $"{bs.Bit}", "ACCESS");
            Emit("MOVF",  Addr(TmpLo), "W", "ACCESS");
            StoreW(bs.Target);
        }
    }

    private void CompileBitClear(BitClear bc)
    {
        if (bc.Target is MemoryAddress mem)
        {
            Emit("BCF", Addr(mem.Address), $"{bc.Bit}", "ACCESS");
            return;
        }
        int addr = AddrOf(bc.Target);
        if (addr >= 0)
        {
            Emit("BCF", Addr(addr), $"{bc.Bit}", "ACCESS");
        }
        else
        {
            LoadIntoW(bc.Target);
            Emit("MOVWF", Addr(TmpLo), "ACCESS");
            Emit("BCF",   Addr(TmpLo), $"{bc.Bit}", "ACCESS");
            Emit("MOVF",  Addr(TmpLo), "W", "ACCESS");
            StoreW(bc.Target);
        }
    }

    private void CompileBitCheck(BitCheck bck)
    {
        int srcAddr = AddrOf(bck.Source);
        string falseL = MakeLabel("L18_BCK_F");
        string doneL  = MakeLabel("L18_BCK_D");

        string f = srcAddr >= 0 ? Addr(srcAddr) : Addr(TmpLo);
        if (srcAddr < 0)
        {
            LoadIntoW(bck.Source);
            Emit("MOVWF", Addr(TmpLo), "ACCESS");
        }

        // BTFSS: skip next instruction if bit is SET.
        // If bit SET → skip GOTO falseL → fall through to set W=1.
        // If bit CLEAR → execute GOTO falseL → set W=0.
        Emit("BTFSS", f, $"{bck.Bit}", "ACCESS");  // skip if SET
        Emit("GOTO",  falseL);                      // bit CLEAR → false
        Emit("MOVLW", "0x01");                      // bit SET → W=1
        Emit("GOTO",  doneL);
        EmitLabel(falseL);
        Emit("CLRW");                               // W=0
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
            Emit("MOVWF", Addr(TmpLo), "ACCESS");
            reg = Addr(TmpLo);
        }
        // Load src into Tmp2Lo to test without disturbing TmpLo.
        LoadIntoW(bw.Src);
        Emit("MOVWF", Addr(Tmp2Lo), "ACCESS");
        Emit("MOVF",  Addr(Tmp2Lo), "F", "ACCESS");  // sets Z from src value
        string skipSet   = MakeLabel("L18_BW_S");
        string skipClear = MakeLabel("L18_BW_C");
        Emit("BZ",   skipSet);                  // src=0 → clear bit
        Emit("BSF",  reg, $"{bw.Bit}", "ACCESS");
        Emit("GOTO", skipClear);
        EmitLabel(skipSet);
        Emit("BCF",  reg, $"{bw.Bit}", "ACCESS");
        EmitLabel(skipClear);
        if (tgtAddr < 0)
        {
            Emit("MOVF",  Addr(TmpLo), "W", "ACCESS");
            StoreW(bw.Target);
        }
    }

    private void CompileJumpIfBitSet(JumpIfBitSet jbs)
    {
        int srcAddr = AddrOf(jbs.Source);
        string f = srcAddr >= 0 ? Addr(srcAddr) : Addr(TmpLo);
        if (srcAddr < 0) { LoadIntoW(jbs.Source); Emit("MOVWF", Addr(TmpLo), "ACCESS"); }
        string skip = MakeLabel("L18_JBS_SK");
        Emit("BTFSC", f, $"{jbs.Bit}", "ACCESS");  // if bit CLEAR, skip GOTO
        Emit("GOTO",  jbs.Target);
    }

    private void CompileJumpIfBitClear(JumpIfBitClear jbc)
    {
        int srcAddr = AddrOf(jbc.Source);
        string f = srcAddr >= 0 ? Addr(srcAddr) : Addr(TmpLo);
        if (srcAddr < 0) { LoadIntoW(jbc.Source); Emit("MOVWF", Addr(TmpLo), "ACCESS"); }
        Emit("BTFSS", f, $"{jbc.Bit}", "ACCESS");  // if bit SET, skip GOTO
        Emit("GOTO",  jbc.Target);
    }

    // -------------------------------------------------------------------------
    // AugAssign  (dst op= operand, equivalent to Binary but in-place)
    // -------------------------------------------------------------------------

    private void CompileAugAssign(AugAssign aa)
    {
        DataType type = GetValType(aa.Target);
        bool is16 = Is16(type);
        int dstAddr = AddrOf(aa.Target);

        if (is16)
        {
            // Load dst into tmp, apply op, write back.
            Load16Into(aa.Target, TmpLo, TmpHi);
            Load16Into(aa.Operand, Tmp2Lo, Tmp2Hi);

            switch (aa.Op)
            {
                case IrBinOp.Add:
                    Emit("MOVF",   Addr(Tmp2Lo), "W", "ACCESS");
                    Emit("ADDWF",  Addr(TmpLo),  "F", "ACCESS");
                    Emit("MOVF",   Addr(Tmp2Hi), "W", "ACCESS");
                    Emit("ADDWFC", Addr(TmpHi),  "F", "ACCESS");
                    break;
                case IrBinOp.Sub:
                    Emit("MOVF",   Addr(Tmp2Lo), "W", "ACCESS");
                    Emit("SUBWF",  Addr(TmpLo),  "F", "ACCESS");
                    Emit("MOVF",   Addr(Tmp2Hi), "W", "ACCESS");
                    Emit("SUBWFB", Addr(TmpHi),  "F", "ACCESS");
                    break;
                case IrBinOp.BitAnd:
                    Emit("MOVF",  Addr(Tmp2Lo), "W", "ACCESS");
                    Emit("ANDWF", Addr(TmpLo),  "F", "ACCESS");
                    Emit("MOVF",  Addr(Tmp2Hi), "W", "ACCESS");
                    Emit("ANDWF", Addr(TmpHi),  "F", "ACCESS");
                    break;
                case IrBinOp.BitOr:
                    Emit("MOVF",  Addr(Tmp2Lo), "W", "ACCESS");
                    Emit("IORWF", Addr(TmpLo),  "F", "ACCESS");
                    Emit("MOVF",  Addr(Tmp2Hi), "W", "ACCESS");
                    Emit("IORWF", Addr(TmpHi),  "F", "ACCESS");
                    break;
                case IrBinOp.BitXor:
                    Emit("MOVF",  Addr(Tmp2Lo), "W", "ACCESS");
                    Emit("XORWF", Addr(TmpLo),  "F", "ACCESS");
                    Emit("MOVF",  Addr(Tmp2Hi), "W", "ACCESS");
                    Emit("XORWF", Addr(TmpHi),  "F", "ACCESS");
                    break;
                default:
                    EmitComment($"16-bit AugAssign {aa.Op} not implemented");
                    break;
            }
            Store16From(TmpLo, TmpHi, aa.Target);
            return;
        }

        // 8-bit AugAssign.
        if (dstAddr >= 0)
        {
            switch (aa.Op)
            {
                case IrBinOp.Add when aa.Operand is Constant { Value: 1 }:
                    Emit("INCF", Addr(dstAddr), "F", "ACCESS");
                    return;
                case IrBinOp.Sub when aa.Operand is Constant { Value: 1 }:
                    Emit("DECF", Addr(dstAddr), "F", "ACCESS");
                    return;
                case IrBinOp.Add:
                    LoadIntoW(aa.Operand);
                    Emit("ADDWF", Addr(dstAddr), "F", "ACCESS");
                    return;
                case IrBinOp.Sub:
                    LoadIntoW(aa.Operand);
                    Emit("SUBWF", Addr(dstAddr), "F", "ACCESS");
                    return;
                case IrBinOp.BitAnd:
                    LoadIntoW(aa.Operand);
                    Emit("ANDWF", Addr(dstAddr), "F", "ACCESS");
                    return;
                case IrBinOp.BitOr:
                    LoadIntoW(aa.Operand);
                    Emit("IORWF", Addr(dstAddr), "F", "ACCESS");
                    return;
                case IrBinOp.BitXor:
                    LoadIntoW(aa.Operand);
                    Emit("XORWF", Addr(dstAddr), "F", "ACCESS");
                    return;
                default:
                    break;
            }
        }

        // Fallback: load→op→store.
        LoadIntoW(aa.Target);
        Emit("MOVWF", Addr(TmpLo), "ACCESS");
        LoadIntoW(aa.Operand);
        switch (aa.Op)
        {
            case IrBinOp.Add:    Emit("ADDWF", Addr(TmpLo), "W", "ACCESS"); break;
            case IrBinOp.Sub:    Emit("SUBWF", Addr(TmpLo), "W", "ACCESS"); break;
            case IrBinOp.BitAnd: Emit("ANDWF", Addr(TmpLo), "W", "ACCESS"); break;
            case IrBinOp.BitOr:  Emit("IORWF", Addr(TmpLo), "W", "ACCESS"); break;
            case IrBinOp.BitXor: Emit("XORWF", Addr(TmpLo), "W", "ACCESS"); break;
            default:
                EmitComment($"AugAssign {aa.Op} not fully implemented");
                break;
        }
        StoreW(aa.Target);
    }

    // -------------------------------------------------------------------------
    // Array load / store  (in-RAM arrays, byte-indexed)
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
            Emit("MOVF", Addr(addr), "W", "ACCESS");
            if (elemSize == 2)
            {
                Emit("MOVWF", Addr(TmpLo), "ACCESS");
                Emit("MOVF",  Addr(addr + 1), "W", "ACCESS");
                Emit("MOVWF", Addr(TmpHi), "ACCESS");
                Store16From(TmpLo, TmpHi, al.Dst);
            }
            else
            {
                StoreW(al.Dst);
            }
        }
        else
        {
            // Variable index: use FSR0 for indirect access.
            EmitComment("ArrayLoad via FSR0 indirect");
            LoadIntoW(al.Index);
            // Compute address: baseAddr + index * elemSize
            if (elemSize == 2) { Emit("ADDWF", "0x00", "W", "ACCESS"); /* double index */ }  // simplified
            Emit("MOVWF", Addr(TmpLo), "ACCESS");  // index in TmpLo
            Emit("MOVLW", $"0x{baseAddr & 0xFF:X2}");
            Emit("ADDWF", Addr(TmpLo), "W", "ACCESS");  // W = base + index
            Emit("MOVWF", "0xFE9", "ACCESS");           // FSR0L = address
            Emit("MOVLW", $"0x{(baseAddr >> 8) & 0xFF:X2}");
            Emit("MOVWF", "0xFEA", "ACCESS");           // FSR0H = high byte of base
            Emit("MOVF",  "0xFEF", "W", "ACCESS");      // INDF0 → W
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
            if (elemSize == 2)
            {
                Load16Into(ast.Src, TmpLo, TmpHi);
                Emit("MOVFF", Addr(TmpLo), Addr(addr));
                Emit("MOVFF", Addr(TmpHi), Addr(addr + 1));
            }
            else
            {
                LoadIntoW(ast.Src);
                Emit("MOVWF", Addr(addr), "ACCESS");
            }
        }
        else
        {
            EmitComment("ArrayStore variable index via FSR0");
            LoadIntoW(ast.Index);
            Emit("MOVWF", Addr(TmpLo), "ACCESS");
            Emit("MOVLW", $"0x{baseAddr & 0xFF:X2}");
            Emit("ADDWF", Addr(TmpLo), "W", "ACCESS");
            Emit("MOVWF", "0xFE9", "ACCESS");   // FSR0L
            Emit("MOVLW", $"0x{(baseAddr >> 8) & 0xFF:X2}");
            Emit("MOVWF", "0xFEA", "ACCESS");   // FSR0H
            LoadIntoW(ast.Src);
            Emit("MOVWF", "0xFEF", "ACCESS");   // INDF0 = W
        }
    }

    // -------------------------------------------------------------------------
    // ArrayLoadRo (PIC18 TBLRD* — table read from program memory)
    // -------------------------------------------------------------------------

    private void CompileArrayLoadRo(ArrayLoadRo alf)
    {
        // Load TBLPTR = base address of the table + index, then TBLRD* to read TABLAT.
        // TBLPTRU (0xFF8), TBLPTRH (0xFF7), TBLPTRL (0xFF6); TABLAT (0xFF5).
        string tableName = $"__flash_{alf.ArrayName.Replace('.', '_')}";
        // Set TBLPTRU = 0 (we assume everything in the first 64K of program memory).
        Emit("CLRF",  "0xFF8", "ACCESS");          // TBLPTRU = 0
        Emit("MOVLW", $"HIGH({tableName})");
        Emit("MOVWF", "0xFF7", "ACCESS");           // TBLPTRH = high byte of label
        // Load low byte = LOW(table) + index.
        LoadIntoW(alf.Index);
        Emit("MOVWF", Addr(TmpLo), "ACCESS");
        Emit("MOVLW", $"LOW({tableName})");
        Emit("ADDWF", Addr(TmpLo), "W", "ACCESS"); // W = LOW(table) + index
        Emit("MOVWF", "0xFF6", "ACCESS");           // TBLPTRL = W
        Emit("TBLRD*");                             // TABLAT = program_memory[TBLPTR]
        Emit("MOVF",  "0xFF5", "W", "ACCESS");      // W = TABLAT
        StoreW(alf.Dst);
    }

    // -------------------------------------------------------------------------
    // Flash data table emission (DB directives in CODE section)
    // -------------------------------------------------------------------------

    private void EmitFlashTables(ProgramIR program)
    {
        var flashDataInstrs = program.Functions
            .SelectMany(f => f.Body)
            .OfType<RoData>()
            .GroupBy(fd => fd.Name)
            .Select(g => g.First());

        foreach (var fd in flashDataInstrs)
        {
            EmitRaw("");
            EmitComment($"Flash data table: {fd.Name} ({fd.Bytes.Count} bytes)");
            string safeName = fd.Name.Replace('.', '_');
            EmitLabel($"__flash_{safeName}");
            // PIC18 assembler DB directive for byte data in CODE section.
            var sb = new System.Text.StringBuilder("\tDB\t");
            for (int i = 0; i < fd.Bytes.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append($"0x{fd.Bytes[i] & 0xFF:X2}");
            }
            EmitRaw(sb.ToString());
        }
    }

    // -------------------------------------------------------------------------
    // Signed comparison helpers (PIC18 — uses N and OV bits in STATUS)
    //
    // After EmitCompare8 (which performs src1 - src2):
    //   N (STATUS bit 4) = 1 if result is negative
    //   OV (STATUS bit 3) = 1 if signed overflow occurred
    //   Z = 1 if equal
    //   Signed LT: N XOR OV = 1
    //   Signed GE: N XOR OV = 0  (i.e., NOT LT)
    // -------------------------------------------------------------------------

    // Materialise boolean 1 when signed src1 < src2 (N XOR OV = 1), else 0.
    private void EmitSignedBoolLT(Val dst)
    {
        string n1Label = MakeLabel("L18_SLTN1");
        string noJump  = MakeLabel("L18_SLT_NO");
        string done    = MakeLabel("L18_SLT_D");
        // N=0, OV=1 → jump (LT); N=1, OV=0 → jump (LT); others → no jump.
        Emit("BNN", n1Label);              // N=0 → go check OV
        // N=1:
        Emit("BOV", noJump);               // N=1, OV=1 → GE → no
        Emit("MOVLW", "0x01");             // N=1, OV=0 → LT → yes
        Emit("GOTO",  done);
        EmitLabel(n1Label);
        // N=0:
        Emit("BNOV", noJump);              // N=0, OV=0 → GE → no
        Emit("MOVLW", "0x01");             // N=0, OV=1 → LT → yes
        Emit("GOTO",  done);
        EmitLabel(noJump);
        Emit("CLRW");
        EmitLabel(done);
        StoreW(dst);
    }

    private void EmitSignedBoolGE(Val dst)
    {
        // GE = NOT LT: N XOR OV = 0.
        string n1Label = MakeLabel("L18_SGEN1");
        string noJump  = MakeLabel("L18_SGE_NO");
        string done    = MakeLabel("L18_SGE_D");
        Emit("BNN", n1Label);
        // N=1:
        Emit("BNOV", noJump);              // N=1, OV=0 → LT → false
        Emit("MOVLW", "0x01");             // N=1, OV=1 → GE → true
        Emit("GOTO",  done);
        EmitLabel(n1Label);
        // N=0:
        Emit("BOV", noJump);               // N=0, OV=1 → LT → false
        Emit("MOVLW", "0x01");             // N=0, OV=0 → GE → true
        Emit("GOTO",  done);
        EmitLabel(noJump);
        Emit("CLRW");
        EmitLabel(done);
        StoreW(dst);
    }

    private void EmitSignedBoolGT(Val dst)
    {
        // GT = GE AND NOT Z.
        string n1Label = MakeLabel("L18_SGTN1");
        string noJump  = MakeLabel("L18_SGT_NO");
        string done    = MakeLabel("L18_SGT_D");
        Emit("BZ",  noJump);               // equal → not GT
        Emit("BNN", n1Label);
        // N=1:
        Emit("BNOV", noJump);
        Emit("MOVLW", "0x01");
        Emit("GOTO",  done);
        EmitLabel(n1Label);
        // N=0:
        Emit("BOV", noJump);
        Emit("MOVLW", "0x01");
        Emit("GOTO",  done);
        EmitLabel(noJump);
        Emit("CLRW");
        EmitLabel(done);
        StoreW(dst);
    }

    private void EmitSignedBoolLE(Val dst)
    {
        // LE = LT OR Z.
        string n1Label = MakeLabel("L18_SLEN1");
        string jumpTrue = MakeLabel("L18_SLE_T");
        string noJump  = MakeLabel("L18_SLE_NO");
        string done    = MakeLabel("L18_SLE_D");
        Emit("BZ",  jumpTrue);             // equal → LE is true
        Emit("BNN", n1Label);
        // N=1:
        Emit("BOV", noJump);               // N=1, OV=1 → GE → false
        Emit("GOTO", jumpTrue);            // N=1, OV=0 → LT → true
        EmitLabel(n1Label);
        // N=0:
        Emit("BNOV", noJump);              // N=0, OV=0 → GE → false
        EmitLabel(jumpTrue);
        Emit("MOVLW", "0x01");
        Emit("GOTO",  done);
        EmitLabel(noJump);
        Emit("CLRW");
        EmitLabel(done);
        StoreW(dst);
    }

    // Signed branch helpers for JumpIf* instructions (PIC18).

    private void BranchIfLessThanSigned(string target)
    {
        // N XOR OV = 1 → jump.
        string checkN0 = MakeLabel("L18_JSLTN0");
        string noJump  = MakeLabel("L18_JSLT_NO");
        Emit("BNN", checkN0);              // N=0 → check OV
        // N=1:
        Emit("BOV", noJump);               // N=1, OV=1 → GE → skip
        Emit("GOTO", target);              // N=1, OV=0 → LT → jump
        EmitLabel(checkN0);
        // N=0:
        Emit("BNOV", noJump);              // N=0, OV=0 → GE → skip
        Emit("GOTO", target);              // N=0, OV=1 → LT → jump
        EmitLabel(noJump);
    }

    private void BranchIfLessOrEqualSigned(string target)
    {
        // LT OR Z.
        string noJump = MakeLabel("L18_JSLE_NO");
        Emit("BZ", target);               // equal → jump
        BranchIfLessThanSigned(target);
    }

    private void BranchIfGreaterThanSigned(string target)
    {
        // GT = GE AND NOT Z.
        string noJump = MakeLabel("L18_JSGT_NO");
        Emit("BZ", noJump);               // equal → skip
        BranchIfGreaterEqualSigned(target);
        EmitLabel(noJump);
    }

    private void BranchIfGreaterEqualSigned(string target)
    {
        // GE = NOT LT: N XOR OV = 0 → jump.
        string checkN0 = MakeLabel("L18_JSGEN0");
        string noJump  = MakeLabel("L18_JSGE_NO");
        Emit("BNN", checkN0);
        // N=1:
        Emit("BNOV", noJump);              // N=1, OV=0 → LT → skip
        Emit("GOTO", target);              // N=1, OV=1 → GE → jump
        EmitLabel(checkN0);
        // N=0:
        Emit("BOV", noJump);               // N=0, OV=1 → LT → skip
        Emit("GOTO", target);              // N=0, OV=0 → GE → jump
        EmitLabel(noJump);
    }

    // -------------------------------------------------------------------------
    // Software divide subroutine (8-bit unsigned)
    //
    // Input:  TmpLo = dividend, Tmp2Lo = divisor
    // Output: TmpLo = quotient, TmpHi = remainder
    // Clobbers: Tmp2Hi (bit counter)
    // -------------------------------------------------------------------------

    private void EmitDiv8Subroutine()
    {
        EmitRaw("");
        EmitComment("8-bit software divide: TmpLo=dividend, Tmp2Lo=divisor");
        EmitComment("Result: TmpLo=quotient, TmpHi=remainder. Clobbers Tmp2Hi.");
        EmitLabel("__pic18_div8");
        Emit("CLRF",    Addr(TmpHi),  "ACCESS");     // remainder = 0
        Emit("MOVLW",   "0x08");
        Emit("MOVWF",   Addr(Tmp2Hi), "ACCESS");     // bit counter = 8
        EmitLabel("__pic18_div8_loop");
        // Rotate left: C = MSB(TmpLo), TmpLo <<= 1; TmpHi = TmpHi<<1|C
        Emit("BCF",     "0xFD8", "0", "ACCESS");     // STATUS.C = 0
        Emit("RLCF",    Addr(TmpLo),  "F", "ACCESS"); // C = MSB(TmpLo), TmpLo <<= 1
        Emit("RLCF",    Addr(TmpHi),  "F", "ACCESS"); // TmpHi = TmpHi<<1|C
        // Test: TmpHi - Tmp2Lo; C=1 if TmpHi >= Tmp2Lo
        Emit("MOVF",    Addr(Tmp2Lo), "W", "ACCESS");
        Emit("SUBWF",   Addr(TmpHi),  "W", "ACCESS"); // W = TmpHi - Tmp2Lo
        Emit("BNC",     "__pic18_div8_nosub");        // C=0 → can't subtract → skip
        Emit("MOVWF",   Addr(TmpHi),  "ACCESS");     // TmpHi = TmpHi - Tmp2Lo
        Emit("BSF",     Addr(TmpLo),  "0", "ACCESS"); // set LSB of TmpLo (quotient bit = 1)
        EmitLabel("__pic18_div8_nosub");
        Emit("DECFSZ",  Addr(Tmp2Hi), "F", "ACCESS"); // count--; skip if 0
        Emit("BRA",     "__pic18_div8_loop");
        Emit("RETURN");
    }
}
