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
using IrBinOp = PyMCU.IR.BinaryOp;
using IrUnOp = PyMCU.IR.UnaryOp;

namespace PyMCU.Backend.Targets.AVR;

public class AvrCodeGen(DeviceConfig cfg) : CodeGen
{
    private readonly List<AvrAsmLine> _assembly = [];
    private Dictionary<string, int> _stackLayout = new();
    private Dictionary<string, int> _varSizes = new();
    private Dictionary<string, string> _regLayout = new();
    private Dictionary<string, string> _tmpRegLayout = new();
    private readonly HashSet<string> _allTmpRegNames = [];
    private readonly Dictionary<string, string> _stringPool = new();
    private bool _uartSendZNeeded;
    private int _labelCounter;
    private Function? _currentFunction;

    private string MakeLabel(string prefix = ".L") => $"{prefix}_{_labelCounter++}";
    private static string GetHighReg(string reg) => "R" + (int.Parse(reg[1..]) + 1);
    private void Emit(string m) => _assembly.Add(AvrAsmLine.MakeInstruction(m));
    private void Emit(string m, string o1) => _assembly.Add(AvrAsmLine.MakeInstruction(m, o1));
    private void Emit(string m, string o1, string o2) => _assembly.Add(AvrAsmLine.MakeInstruction(m, o1, o2));
    private void EmitLabel(string l) => _assembly.Add(AvrAsmLine.MakeLabel(l));
    private void EmitComment(string c) => _assembly.Add(AvrAsmLine.MakeComment(c));
    private void EmitRaw(string t) => _assembly.Add(AvrAsmLine.MakeRaw(t));

    private static string ResolveAddress(Val val)
    {
        switch (val)
        {
            case Constant c:
                return $"{c.Value & 0xFF}";
            case MemoryAddress mem:
                return $"0x{mem.Address:X4}";
            default:
            {
                var name = val switch { Variable v => v.Name, Temporary t => t.Name, _ => "" };
                return name.Replace('.', '_');
            }
        }
    }

    private static DataType GetValType(Val val) => val switch
    {
        Variable v => v.Type,
        Temporary t => t.Type,
        MemoryAddress m => m.Type.SizeOf() > 1 ? m.Type : DataType.UINT8,
        Constant { Value: > 255 or < -128 } => DataType.UINT16,
        _ => DataType.UINT8,
    };

    private static bool IsSignedType(DataType t) => t.IsSigned();

    // Returns true if the comparison should use signed branches (BRLT/BRGE).
    // Negative constants indicate a signed context even when type info is lost by folding.
    private static bool IsSignedComparison(Val src1, Val src2)
    {
        if (IsSignedType(GetValType(src1)) || IsSignedType(GetValType(src2))) return true;
        if (src1 is Constant c1 && c1.Value < 0) return true;
        if (src2 is Constant c2 && c2.Value < 0) return true;
        return false;
    }

    private void EmitBranch(string cond, string target)
    {
        var inv = new Dictionary<string, string>
        {
            { "BREQ", "BRNE" }, { "BRNE", "BREQ" }, { "BRLT", "BRGE" }, { "BRGE", "BRLT" },
            { "BRCS", "BRCC" }, { "BRCC", "BRCS" }, { "BRLO", "BRSH" }, { "BRSH", "BRLO" },
        };
        string inverted = inv.GetValueOrDefault(cond, cond);
        string skip = MakeLabel("L_BR_SKIP");
        Emit(inverted, skip);
        Emit("RJMP", target);
        EmitLabel(skip);
    }

    private void LoadIntoReg(Val val, string reg, DataType type = DataType.UINT8)
    {
        int size = type.SizeOf();
        var regH  = size >= 2 ? GetHighReg(reg) : "";
        // For 32-bit: byte2=R22, byte3=R23 (AVR-GCC uint32 convention when base=R24)
        // When base is not R24, fall back to reg+2/+3 (not used for 32-bit in practice)
        var regB2 = size == 4 ? (reg == "R24" ? "R22" : $"R{int.Parse(reg[1..]) + 2}") : "";
        var regB3 = size == 4 ? (reg == "R24" ? "R23" : $"R{int.Parse(reg[1..]) + 3}") : "";

        switch (val)
        {
            case Constant c:
            {
                Emit("LDI", reg, $"{c.Value & 0xFF}");
                if (size >= 2) Emit("LDI", regH, $"{(c.Value >> 8) & 0xFF}");
                if (size == 4) { Emit("LDI", regB2, $"{(c.Value >> 16) & 0xFF}"); Emit("LDI", regB3, $"{(c.Value >> 24) & 0xFF}"); }
                return;
            }
            case MemoryAddress mem:
            {
                if (mem.Address is >= 0x20 and <= 0x5F)
                    Emit("IN", reg, $"0x{mem.Address - 0x20:X2}");
                else
                    Emit("LDS", reg, $"0x{mem.Address:X4}");
                if (size >= 2) Emit("LDS", regH,  $"0x{mem.Address + 1:X4}");
                if (size == 4) { Emit("LDS", regB2, $"0x{mem.Address + 2:X4}"); Emit("LDS", regB3, $"0x{mem.Address + 3:X4}"); }
                return;
            }
        }

        var name = val switch { Variable v2 => v2.Name, Temporary t2 => t2.Name, _ => "" };

        if (!string.IsNullOrEmpty(name) && _regLayout.TryGetValue(name, out var srcReg))
        {
            DataType sourceType = GetValType(val);
            bool needSignExt = size == 2 && sourceType.SizeOf() == 1 && IsSignedType(sourceType);

            if (srcReg != reg) Emit("MOV", reg, srcReg);
            else if (!needSignExt && srcReg == reg)
            {
                // Source already in target reg; still need to populate high bytes if multi-byte
                if (size >= 2) Emit("MOV", regH, GetHighReg(srcReg));
                if (size == 4) { Emit("MOV", regB2, $"R{int.Parse(srcReg[1..]) + 2}"); Emit("MOV", regB3, $"R{int.Parse(srcReg[1..]) + 3}"); }
                return;
            }

            if (size >= 2 && !needSignExt) Emit("MOV", regH, GetHighReg(srcReg));
            if (size == 4) { Emit("MOV", regB2, $"R{int.Parse(srcReg[1..]) + 2}"); Emit("MOV", regB3, $"R{int.Parse(srcReg[1..]) + 3}"); }

            if (needSignExt)
            {
                Emit("MOV", regH, reg);
                Emit("LSL", regH);
                Emit("SBC", regH, regH);
            }
            return;
        }

        if (!string.IsNullOrEmpty(name) && _tmpRegLayout.TryGetValue(name, out var tmpReg))
        {
            DataType sourceType = GetValType(val);
            bool needSignExt = size == 2 && sourceType.SizeOf() == 1 && IsSignedType(sourceType);

            if (tmpReg != reg) Emit("MOV", reg, tmpReg);
            if (size >= 2 && !needSignExt) Emit("MOV", regH, GetHighReg(tmpReg));
            if (size == 4) { Emit("MOV", regB2, $"R{int.Parse(tmpReg[1..]) + 2}"); Emit("MOV", regB3, $"R{int.Parse(tmpReg[1..]) + 3}"); }

            if (needSignExt)
            {
                Emit("MOV", regH, reg);
                Emit("LSL", regH);
                Emit("SBC", regH, regH);
            }
            return;
        }

        if (!string.IsNullOrEmpty(name) && _stackLayout.TryGetValue(name, out int offset))
        {
            bool nearY = offset + (size - 1) < 64;
            DataType sourceType = GetValType(val);
            bool needSignExt = size == 2 && sourceType.SizeOf() == 1 && IsSignedType(sourceType);

            if (nearY)
            {
                Emit("LDD", reg, $"Y+{offset}");
                if (size >= 2 && !needSignExt) Emit("LDD", regH,  $"Y+{offset + 1}");
                if (size == 4) { Emit("LDD", regB2, $"Y+{offset + 2}"); Emit("LDD", regB3, $"Y+{offset + 3}"); }
            }
            else
            {
                var abs = 0x0100 + offset;
                Emit("LDS", reg, $"0x{abs:X4}");
                if (size >= 2 && !needSignExt) Emit("LDS", regH,  $"0x{abs + 1:X4}");
                if (size == 4) { Emit("LDS", regB2, $"0x{abs + 2:X4}"); Emit("LDS", regB3, $"0x{abs + 3:X4}"); }
            }

            if (needSignExt)
            {
                Emit("MOV", regH, reg);
                Emit("LSL", regH);
                Emit("SBC", regH, regH);
            }
            return;
        }

        var addr = ResolveAddress(val);
        if (string.IsNullOrEmpty(addr)) return;
        DataType srcType = GetValType(val);
        bool signExt = size == 2 && srcType.SizeOf() == 1 && IsSignedType(srcType);

        Emit("LDS", reg, addr);
        if (size >= 2 && !signExt) Emit("LDS", regH, addr + "+1");
        if (size == 4) { Emit("LDS", regB2, addr + "+2"); Emit("LDS", regB3, addr + "+3"); }

        if (signExt)
        {
            Emit("MOV", regH, reg);
            Emit("LSL", regH);
            Emit("SBC", regH, regH);
        }
    }

    private void StoreRegInto(string reg, Val val, DataType type = DataType.UINT8)
    {
        if (val is Constant) return;
        int size = type.SizeOf();
        var regH  = size >= 2 ? GetHighReg(reg) : "";
        var regB2 = size == 4 ? (reg == "R24" ? "R22" : $"R{int.Parse(reg[1..]) + 2}") : "";
        var regB3 = size == 4 ? (reg == "R24" ? "R23" : $"R{int.Parse(reg[1..]) + 3}") : "";

        if (val is MemoryAddress mem)
        {
            if (mem.Address is >= 0x20 and <= 0x5F)
                Emit("OUT", $"0x{mem.Address - 0x20:X2}", reg);
            else
                Emit("STS", $"0x{mem.Address:X4}", reg);
            if (size >= 2) Emit("STS", $"0x{mem.Address + 1:X4}", regH);
            if (size == 4) { Emit("STS", $"0x{mem.Address + 2:X4}", regB2); Emit("STS", $"0x{mem.Address + 3:X4}", regB3); }
            return;
        }

        var name = val switch { Variable v => v.Name, Temporary t => t.Name, _ => "" };

        if (!string.IsNullOrEmpty(name) && _regLayout.TryGetValue(name, out var dstReg))
        {
            if (dstReg != reg) Emit("MOV", dstReg, reg);
            if (size >= 2) Emit("MOV", GetHighReg(dstReg), regH);
            if (size == 4) { Emit("MOV", $"R{int.Parse(dstReg[1..]) + 2}", regB2); Emit("MOV", $"R{int.Parse(dstReg[1..]) + 3}", regB3); }
            return;
        }

        if (!string.IsNullOrEmpty(name) && _tmpRegLayout.TryGetValue(name, out var tmpReg))
        {
            if (tmpReg != reg) Emit("MOV", tmpReg, reg);
            if (size >= 2) Emit("MOV", GetHighReg(tmpReg), regH);
            if (size == 4) { Emit("MOV", $"R{int.Parse(tmpReg[1..]) + 2}", regB2); Emit("MOV", $"R{int.Parse(tmpReg[1..]) + 3}", regB3); }
            return;
        }

        if (!string.IsNullOrEmpty(name) && _stackLayout.TryGetValue(name, out int offset))
        {
            bool nearY = offset + (size - 1) < 64;
            if (nearY)
            {
                Emit("STD", $"Y+{offset}", reg);
                if (size >= 2) Emit("STD", $"Y+{offset + 1}", regH);
                if (size == 4) { Emit("STD", $"Y+{offset + 2}", regB2); Emit("STD", $"Y+{offset + 3}", regB3); }
            }
            else
            {
                var abs = 0x0100 + offset;
                Emit("STS", $"0x{abs:X4}", reg);
                if (size >= 2) Emit("STS", $"0x{abs + 1:X4}", regH);
                if (size == 4) { Emit("STS", $"0x{abs + 2:X4}", regB2); Emit("STS", $"0x{abs + 3:X4}", regB3); }
            }
            return;
        }

        var addr = ResolveAddress(val);
        if (string.IsNullOrEmpty(addr)) return;
        Emit("STS", addr, reg);
        if (size >= 2) Emit("STS", addr + "+1", regH);
        if (size == 4) { Emit("STS", addr + "+2", regB2); Emit("STS", addr + "+3", regB3); }
    }

    public override void Compile(ProgramIR program, TextWriter output)
    {
        _assembly.Clear();
        _stringPool.Clear();
        _uartSendZNeeded = false;
        _allTmpRegNames.Clear();
        _labelCounter = 0;

        var allocator = new StackAllocator();
        var (offsets, _) = allocator.Allocate(program);
        _stackLayout = offsets;
        _varSizes = allocator.VariableSizes;
        _regLayout = AvrRegisterAllocator.Allocate(program);

        EmitComment("Generated by pymcuc for " + cfg.Chip);

        foreach (var sym in program.ExternSymbols)
            EmitRaw(".extern " + sym);
        if (program.ExternSymbols.Count > 0) EmitRaw("");

        EmitRaw(".equ RAMSTART, 0x0100");
        EmitRaw(".equ _stack_base, RAMSTART");

        foreach (var (name, offset) in _stackLayout)
        {
            if (_regLayout.ContainsKey(name)) continue;
            if (_allTmpRegNames.Contains(name)) continue;
            var safeName = name.Replace('.', '_');
            EmitRaw($".equ {safeName}, _stack_base + {offset}");
        }

        EmitRaw("");

        // ISR map
        var isrMap = new SortedDictionary<int, Function>();
        foreach (var func in program.Functions.Where(func => func.IsInterrupt))
        {
            // Add duplicate ISR check that was missing in the C# port
            if (!isrMap.TryAdd(func.InterruptVector, func))
            {
                throw new Exception($"Multiple ISRs defined for vector 0x{func.InterruptVector:X4}");
            }
        }

        EmitRaw(".org 0x0000");
        EmitRaw(".global main");
        Emit("RJMP", "main");

        if (isrMap.Count > 0)
        {
            for (var vec = 1; vec <= 25; vec++)
            {
                // AVR8Sharp sets cpu.Pc = overflowInterrupt (a byte address on real hardware,
                // e.g. 0x12 for Timer2 OVF). Since ProgramMemory is word-indexed, cpu.Pc=0x12
                // executes from byte 0x24 (= 2 × 0x12).
                // AVRA .org uses WORD addresses, and _avra_to_gnuas() multiplies by 2:
                //   AVRA .org 0x0012 → avr-as .org 0x0024 (byte).
                // To place RJMP at byte 0x0024, we need AVRA .org = 0x0012 = vec*2.
                // This matches overflowInterrupt = vec*2 (the byte address on real hardware).
                EmitRaw($".org 0x{vec * 2:X4}");

                if (isrMap.TryGetValue(vec * 2, out var isrFunc))
                {
                    Emit("RJMP", isrFunc.Name);
                }
                else
                {
                    Emit("RETI");
                }
            }

            EmitRaw("");
        }

        foreach (var func in program.Functions.Where(func => func.IsInterrupt))
            CompileFunction(func);
        foreach (var func in program.Functions.Where(func => !func.IsInterrupt)
                     .Where(func => !func.IsInline || func.Name == "main"))
        {
            CompileFunction(func);
        }

        var optimized = AvrPeephole.Optimize(_assembly);
        foreach (var line in optimized)
            output.WriteLine(line.ToString());

        EmitStringPool(output);
    }

    public override void EmitContextSave()
    {
        EmitComment("ISR prologue -- save context");
        // R0 is clobbered by every MUL; R1 is the zero register assumed by SBC/ADC after MUL.
        // avr-gcc saves both in every ISR to prevent corruption of the interrupted context.
        Emit("PUSH", "R0");
        Emit("PUSH", "R1");
        Emit("PUSH", "R16");
        Emit("PUSH", "R17");
        Emit("PUSH", "R18");
        Emit("PUSH", "R19");
        Emit("PUSH", "R24");
        Emit("PUSH", "R25");
        Emit("PUSH", "R26");
        Emit("PUSH", "R27");
        Emit("IN", "R16", "0x3F");
        Emit("PUSH", "R16");
        // Ensure R1 == 0 inside the ISR body (MUL may have left it non-zero in main).
        Emit("CLR", "R1");
    }

    public override void EmitContextRestore()
    {
        EmitComment("ISR epilogue -- restore context");
        Emit("POP", "R16");
        Emit("OUT", "0x3F", "R16");
        Emit("POP", "R27");
        Emit("POP", "R26");
        Emit("POP", "R25");
        Emit("POP", "R24");
        Emit("POP", "R19");
        Emit("POP", "R18");
        Emit("POP", "R17");
        Emit("POP", "R16");
        Emit("POP", "R1");
        Emit("POP", "R0");
    }

    public override void EmitInterruptReturn() => Emit("RETI");

    private void CompileFunction(Function func)
    {
        _currentFunction = func;
        _tmpRegLayout = AvrLinearScan.Allocate(func);
        foreach (var (name, _) in _tmpRegLayout)
            _allTmpRegNames.Add(name);

        EmitLabel(func.Name);

        if (func.IsInterrupt) EmitContextSave();

        if (func.Name == "main")
        {
            Emit("LDI", "R16", "hi8(0x08FF)");
            Emit("OUT", "0x3E", "R16");
            Emit("LDI", "R16", "lo8(0x08FF)");
            Emit("OUT", "0x3D", "R16");
            Emit("LDI", "R28", "lo8(_stack_base)");
            Emit("LDI", "R29", "hi8(_stack_base)");
        }

        if (!func.IsInterrupt && func.Name != "main" && func.Params.Count > 0)
        {
            string[] argRegs = ["R24", "R22", "R20", "R18"];
            for (var k = 0; k < func.Params.Count && k < 4; k++)
            {
                var pname = func.Params[k];
                bool p16 = _varSizes.TryGetValue(pname, out int psz) && psz == 2;
                bool p32 = _varSizes.TryGetValue(pname, out int psz32) && psz32 == 4;
                // For uint32, param k=0 occupies R24:R25:R22:R23 (not argRegs[k] alone).
                // argRegs array is for separate parameters; a uint32 first arg spans R24-R23.
                string aR = argRegs[k];
                if (_regLayout.TryGetValue(pname, out var r))
                {
                    if (aR != r) Emit("MOV", r, aR);
                    if (p16 || p32) Emit("MOV", GetHighReg(r), GetHighReg(aR));
                    if (p32)
                    {
                        // bytes 2 and 3 are in R22 and R23 when k==0 (first arg)
                        string aR2 = k == 0 ? "R22" : $"R{int.Parse(aR[1..]) + 2}";
                        string aR3 = k == 0 ? "R23" : $"R{int.Parse(aR[1..]) + 3}";
                        Emit("MOV", $"R{int.Parse(r[1..]) + 2}", aR2);
                        Emit("MOV", $"R{int.Parse(r[1..]) + 3}", aR3);
                    }
                }
                else if (_stackLayout.TryGetValue(pname, out int off))
                {
                    Emit("STD", $"Y+{off}", aR);
                    if (p16 || p32) Emit("STD", $"Y+{off + 1}", GetHighReg(aR));
                    if (p32)
                    {
                        string aR2 = k == 0 ? "R22" : $"R{int.Parse(aR[1..]) + 2}";
                        string aR3 = k == 0 ? "R23" : $"R{int.Parse(aR[1..]) + 3}";
                        Emit("STD", $"Y+{off + 2}", aR2);
                        Emit("STD", $"Y+{off + 3}", aR3);
                    }
                }
            }
        }

        bool emittedEpilogue = false;
        foreach (var instr in func.Body)
        {
            if (func.IsInterrupt && instr is Return)
            {
                EmitContextRestore();
                Emit("RETI");
                emittedEpilogue = true;
                continue;
            }

            CompileInstruction(instr);
        }

        if (func.IsInterrupt && !emittedEpilogue)
        {
            EmitContextRestore();
            Emit("RETI");
        }
    }

    private void CompileInstruction(Instruction instr)
    {
        switch (instr)
        {
            case Return r: CompileReturn(r); break;
            case Jump j: Emit("RJMP", j.Target); break;
            case JumpIfZero jz: CompileJumpIfZero(jz); break;
            case JumpIfNotZero jnz: CompileJumpIfNotZero(jnz); break;
            case Label l: EmitLabel(l.Name); break;
            case DebugLine d:
                EmitComment(string.IsNullOrEmpty(d.SourceFile)
                    ? $"Line {d.Line}: {d.Text}"
                    : $"{d.SourceFile}:{d.Line}: {d.Text}"); break;
            case JumpIfEqual je: CompileCompareJump(je.Src1, je.Src2, "BREQ", je.Target); break;
            case JumpIfNotEqual jne: CompileCompareJump(jne.Src1, jne.Src2, "BRNE", jne.Target); break;
            case JumpIfLessThan jlt: CompileCompareJump(jlt.Src1, jlt.Src2, IsSignedComparison(jlt.Src1, jlt.Src2) ? "BRLT" : "BRLO", jlt.Target); break;
            case JumpIfLessOrEqual jle: CompileLessOrEqual(jle); break;
            case JumpIfGreaterThan jgt: CompileGreaterThan(jgt); break;
            case JumpIfGreaterOrEqual jge: CompileCompareJump(jge.Src1, jge.Src2, IsSignedComparison(jge.Src1, jge.Src2) ? "BRGE" : "BRSH", jge.Target); break;
            case Call c: CompileCall(c); break;
            case Copy cp: CompileCopy(cp); break;
            case LoadIndirect li: CompileLoadIndirect(li); break;
            case StoreIndirect si: CompileStoreIndirect(si); break;
            case Unary u: CompileUnary(u); break;
            case Binary b: CompileBinary(b); break;
            case BitSet bs: CompileBitSet(bs); break;
            case BitClear bc: CompileBitClear(bc); break;
            case BitCheck bck: CompileBitCheck(bck); break;
            case BitWrite bw: CompileBitWrite(bw); break;
            case JumpIfBitSet jbs: CompileJumpIfBitSet(jbs); break;
            case JumpIfBitClear jbc: CompileJumpIfBitClear(jbc); break;
            case AugAssign aa: CompileAugAssign(aa); break;
            case InlineAsm asm2: _assembly.Add(AvrAsmLine.MakeRaw(asm2.Code)); break;
            case ArrayLoad al: CompileArrayLoad(al); break;
            case ArrayStore ast: CompileArrayStore(ast); break;
            case UARTSendString us: CompileUartSendString(us); break;
        }
    }

    private void CompileReturn(Return r)
    {
        if (r.Value is not NoneVal)
        {
            var returnType = _currentFunction?.ReturnType ?? GetValType(r.Value);
            LoadIntoReg(r.Value, "R24", returnType);
        }

        Emit("RET");
    }

    private void CompileJumpIfZero(JumpIfZero jz)
    {
        var type = GetValType(jz.Condition);
        LoadIntoReg(jz.Condition, "R24", type);

        if (type.SizeOf() == 2)
        {
            Emit("OR", "R24", "R25"); // Combine low and high, this sets the Z flag
            EmitBranch("BREQ", jz.Target);
        }
        else
        {
            Emit("TST", "R24"); // Only test if it's an 8-bit value
            EmitBranch("BREQ", jz.Target);
        }
    }

    private void CompileJumpIfNotZero(JumpIfNotZero jnz)
    {
        var type = GetValType(jnz.Condition);
        LoadIntoReg(jnz.Condition, "R24", type);
        // OR R24, R25 already sets the Z flag for 16-bit values; no separate TST needed.
        if (type.SizeOf() == 2) Emit("OR", "R24", "R25");
        else Emit("TST", "R24");
        EmitBranch("BRNE", jnz.Target);
    }

    private void EmitCompare(Val src1, Val src2, DataType type)
    {
        LoadIntoReg(src1, "R24", type);
        if (src2 is Constant c)
        {
            var val = c.Value;
            if (type.SizeOf() == 2)
            {
                Emit("LDI", "R18", $"{val & 0xFF}");
                Emit("LDI", "R19", $"{(val >> 8) & 0xFF}");
                Emit("CP", "R24", "R18");
                Emit("CPC", "R25", "R19");
            }
            else Emit("CPI", "R24", $"{val & 0xFF}");
        }
        else
        {
            LoadIntoReg(src2, "R18", type);
            Emit("CP", "R24", "R18");
            if (type.SizeOf() == 2) Emit("CPC", "R25", "R19");
        }
    }

    private void CompileCompareJump(Val src1, Val src2, string branch, string target)
    {
        var type = GetValType(src1);
        EmitCompare(src1, src2, type);
        EmitBranch(branch, target);
    }

    private void CompileLessOrEqual(JumpIfLessOrEqual jle)
    {
        var type = GetValType(jle.Src1);
        EmitCompare(jle.Src1, jle.Src2, type);
        string brLo = IsSignedComparison(jle.Src1, jle.Src2) ? "BRLT" : "BRLO";
        EmitBranch(brLo, jle.Target);
        EmitBranch("BREQ", jle.Target);
    }

    private void CompileGreaterThan(JumpIfGreaterThan jgt)
    {
        var type = GetValType(jgt.Src1);
        bool signed = IsSignedComparison(jgt.Src1, jgt.Src2);
        LoadIntoReg(jgt.Src1, "R24", type);

        if (jgt.Src2 is Constant c)
        {
            int val = c.Value;
            int maxVal = type.SizeOf() == 2 ? (signed ? 0x7FFF : 0xFFFF) : (signed ? 0x7F : 0xFF);
            if (val < maxVal)
            {
                int cmpVal = val + 1;
                if (type.SizeOf() == 2)
                {
                    Emit("LDI", "R18", $"{cmpVal & 0xFF}");
                    Emit("LDI", "R19", $"{(cmpVal >> 8) & 0xFF}");
                    Emit("CP", "R24", "R18");
                    Emit("CPC", "R25", "R19");
                }
                else Emit("CPI", "R24", $"{cmpVal & 0xFF}");

                EmitBranch(signed ? "BRGE" : "BRSH", jgt.Target);
            }

            return; // a > max is always false
        }

        LoadIntoReg(jgt.Src2, "R18", type);
        Emit("CP", "R24", "R18");
        if (type.SizeOf() == 2) Emit("CPC", "R25", "R19");
        var skip = MakeLabel("L_BRHI_SKIP");
        Emit("BREQ", skip);
        EmitBranch(signed ? "BRGE" : "BRSH", jgt.Target);
        EmitLabel(skip);
    }

    private void CompileCall(Call call)
    {
        string[] argRegs = ["R24", "R22", "R20", "R18"];
        for (var k = 0; k < call.Args.Count && k < 4; k++)
        {
            var argType = GetValType(call.Args[k]);
            LoadIntoReg(call.Args[k], argRegs[k], argType);
        }

        Emit("CALL", call.FunctionName);
        var dstType = GetValType(call.Dst);
        StoreRegInto("R24", call.Dst, dstType);
    }

    private void CompileCopy(Copy cp)
    {
        // When src is a typeless constant, use the destination's declared type
        // to ensure e.g. `i: uint16 = 0` initialises both bytes.
        var srcType = GetValType(cp.Src);
        var dstType = GetValType(cp.Dst);
        // Use destination type for size, but LoadIntoReg will check source type for sign-extension
        var loadType = cp.Src is Constant ? dstType : dstType;
        LoadIntoReg(cp.Src, "R24", loadType);
        StoreRegInto("R24", cp.Dst, dstType);
    }

    private void CompileLoadIndirect(LoadIndirect li)
    {
        LoadIntoReg(li.SrcPtr, "R26", DataType.UINT16);
        DataType dstType = GetValType(li.Dst);
        int dstSize = dstType.SizeOf();
        if (dstSize == 4)
        {
            Emit("LD", "R24", "X+");
            Emit("LD", "R25", "X+");
            Emit("LD", "R22", "X+");
            Emit("LD", "R23", "X");
        }
        else if (dstSize == 2)
        {
            Emit("LD", "R24", "X+");
            Emit("LD", "R25", "X");
        }
        else Emit("LD", "R24", "X");
        StoreRegInto("R24", li.Dst, dstType);
    }

    private void CompileStoreIndirect(StoreIndirect si)
    {
        LoadIntoReg(si.DstPtr, "R26", DataType.UINT16);
        DataType srcType = GetValType(si.Src);
        LoadIntoReg(si.Src, "R24", srcType);
        int srcSize = srcType.SizeOf();
        if (srcSize == 4)
        {
            Emit("ST", "X+", "R24");
            Emit("ST", "X+", "R25");
            Emit("ST", "X+", "R22");
            Emit("ST", "X",  "R23");
        }
        else if (srcSize == 2)
        {
            Emit("ST", "X+", "R24");
            Emit("ST", "X",  "R25");
        }
        else Emit("ST", "X", "R24");
    }

    private void CompileUnary(Unary u)
    {
        DataType type = GetValType(u.Dst);
        LoadIntoReg(u.Src, "R24", type);
        bool is16 = type.SizeOf() == 2;
        bool is32 = type.SizeOf() == 4;

        switch (u.Op)
        {
            case IrUnOp.Neg:
                // Two's-complement negation using the NEG/COM/SBCI carry-chain.
                // NEG R24 sets C = (R24_original != 0).
                // Each subsequent byte: COM Rn ; SBCI Rn, 255
                //   computes ~Rn + 1 - C, which is the correct borrow-propagated byte.
                // avr-gcc emits the identical sequence for all widths.
                Emit("NEG", "R24");
                if (is16 || is32)
                {
                    Emit("COM", "R25");
                    Emit("SBCI", "R25", "255");
                }
                if (is32)
                {
                    Emit("COM", "R22");
                    Emit("SBCI", "R22", "255");
                    Emit("COM", "R23");
                    Emit("SBCI", "R23", "255");
                }
                break;
            case IrUnOp.BitNot:
                Emit("COM", "R24");
                if (is16 || is32) Emit("COM", "R25");
                if (is32) { Emit("COM", "R22"); Emit("COM", "R23"); }
                break;
            case IrUnOp.Not:
                var lTrue = MakeLabel("L_NOT_TRUE");
                var lDone = MakeLabel("L_NOT_DONE");
                if (is16) Emit("OR", "R24", "R25");
                Emit("TST", "R24");
                EmitBranch("BREQ", lTrue);
                Emit("CLR", "R24");
                if (is16) Emit("CLR", "R25");
                Emit("RJMP", lDone);
                EmitLabel(lTrue);
                Emit("LDI", "R24", "1");
                EmitLabel(lDone);
                break;
        }

        StoreRegInto("R24", u.Dst, type);
    }

    private void CompileBinary(Binary b)
    {
        DataType type = GetValType(b.Dst);
        bool is16 = type.SizeOf() == 2;
        bool is32 = type.SizeOf() == 4;
        LoadIntoReg(b.Src1, "R24", type);

        bool usedImm = false;
        if (b.Src2 is Constant c2)
        {
            int val = c2.Value;
            if (is32)
            {
                switch (b.Op)
                {
                    case IrBinOp.BitAnd:
                        Emit("ANDI", "R24", $"{val & 0xFF}");
                        Emit("ANDI", "R25", $"{(val >> 8) & 0xFF}");
                        Emit("ANDI", "R22", $"{(val >> 16) & 0xFF}");
                        Emit("ANDI", "R23", $"{(val >> 24) & 0xFF}");
                        usedImm = true;
                        break;
                    case IrBinOp.BitOr:
                        Emit("ORI", "R24", $"{val & 0xFF}");
                        Emit("ORI", "R25", $"{(val >> 8) & 0xFF}");
                        Emit("ORI", "R22", $"{(val >> 16) & 0xFF}");
                        Emit("ORI", "R23", $"{(val >> 24) & 0xFF}");
                        usedImm = true;
                        break;
                    case IrBinOp.RShift:
                    {
                        int byteShift = val / 8;
                        int bitShift  = val % 8;
                        bool s32 = IsSignedType(type);
                        if (byteShift >= 4)
                        {
                            if (s32) { Emit("MOV","R24","R23"); Emit("LSL","R24"); Emit("SBC","R24","R24"); Emit("MOV","R25","R24"); Emit("MOV","R22","R24"); Emit("MOV","R23","R24"); }
                            else { Emit("CLR","R24"); Emit("CLR","R25"); Emit("CLR","R22"); Emit("CLR","R23"); }
                        }
                        else if (byteShift == 3) { Emit("MOV","R24","R23"); Emit("CLR","R25"); Emit("CLR","R22"); Emit("CLR","R23"); }
                        else if (byteShift == 2) { Emit("MOV","R24","R22"); Emit("MOV","R25","R23"); Emit("CLR","R22"); Emit("CLR","R23"); }
                        else if (byteShift == 1) { Emit("MOV","R24","R25"); Emit("MOV","R25","R22"); Emit("MOV","R22","R23"); Emit("CLR","R23"); }
                        for (int i = 0; i < bitShift; i++)
                        {
                            if (s32) Emit("ASR","R23"); else Emit("LSR","R23");
                            Emit("ROR","R22"); Emit("ROR","R25"); Emit("ROR","R24");
                        }
                        usedImm = true;
                        break;
                    }
                    case IrBinOp.LShift:
                    {
                        int byteShift = val / 8;
                        int bitShift  = val % 8;
                        if (byteShift >= 4) { Emit("CLR","R24"); Emit("CLR","R25"); Emit("CLR","R22"); Emit("CLR","R23"); }
                        else if (byteShift == 3) { Emit("MOV","R23","R24"); Emit("CLR","R24"); Emit("CLR","R25"); Emit("CLR","R22"); }
                        else if (byteShift == 2) { Emit("MOV","R23","R25"); Emit("MOV","R22","R24"); Emit("CLR","R24"); Emit("CLR","R25"); }
                        else if (byteShift == 1) { Emit("MOV","R23","R22"); Emit("MOV","R22","R25"); Emit("MOV","R25","R24"); Emit("CLR","R24"); }
                        for (int i = 0; i < bitShift; i++) { Emit("LSL","R24"); Emit("ROL","R25"); Emit("ROL","R22"); Emit("ROL","R23"); }
                        usedImm = true;
                        break;
                    }
                    case IrBinOp.Add:
                    {
                        int neg = -val;
                        Emit("SUBI", "R24", $"{(byte)(neg & 0xFF)}");
                        Emit("SBCI", "R25", $"{(byte)((neg >> 8) & 0xFF)}");
                        Emit("SBCI", "R22", $"{(byte)((neg >> 16) & 0xFF)}");
                        Emit("SBCI", "R23", $"{(byte)((neg >> 24) & 0xFF)}");
                        usedImm = true;
                        break;
                    }
                    case IrBinOp.Sub:
                        Emit("SUBI", "R24", $"{val & 0xFF}");
                        Emit("SBCI", "R25", $"{(val >> 8) & 0xFF}");
                        Emit("SBCI", "R22", $"{(val >> 16) & 0xFF}");
                        Emit("SBCI", "R23", $"{(val >> 24) & 0xFF}");
                        usedImm = true;
                        break;
                }
            }
            else if (!is16)
            {
                switch (b.Op)
                {
                    case IrBinOp.Add:
                        if (val == 1) Emit("INC", "R24");
                        else if (val == 255) Emit("DEC", "R24");
                        else Emit("SUBI", "R24", $"{(byte)(-val)}");
                        usedImm = true;
                        break;
                    case IrBinOp.Sub:
                        if (val == 1) Emit("DEC", "R24");
                        else if (val == 255) Emit("INC", "R24");
                        else Emit("SUBI", "R24", $"{val & 0xFF}");
                        usedImm = true;
                        break;
                    case IrBinOp.BitAnd:
                        Emit("ANDI", "R24", $"{val & 0xFF}");
                        usedImm = true;
                        break;
                    case IrBinOp.BitOr:
                        Emit("ORI", "R24", $"{val & 0xFF}");
                        usedImm = true;
                        break;
                    case IrBinOp.LShift:
                        for (int i = 0; i < (val & 7); i++) Emit("LSL", "R24");
                        usedImm = true;
                        break;
                    case IrBinOp.RShift:
                        for (int i = 0; i < (val & 7); i++)
                            if (IsSignedType(type)) Emit("ASR", "R24"); else Emit("LSR", "R24");
                        usedImm = true;
                        break;
                }
            }
            else
            {
                switch (b.Op)
                {
                    case IrBinOp.Add:
                        // ADIW R24, k is a 1-word, 2-cycle instruction for k in 1..63.
                        // SUBI+SBCI is 2 words / 4 cycles.  ADIW also handles k=0 (NOP-equivalent).
                        if (val >= 0 && val <= 63)
                            Emit("ADIW", "R24", $"{val}");
                        else if (val >= -63 && val < 0)
                            Emit("SBIW", "R24", $"{-val}");
                        else { int neg = -val; Emit("SUBI", "R24", $"{(byte)(neg & 0xFF)}"); Emit("SBCI", "R25", $"{(byte)((neg >> 8) & 0xFF)}"); }
                        usedImm = true;
                        break;
                    case IrBinOp.Sub:
                        if (val >= 0 && val <= 63)
                            Emit("SBIW", "R24", $"{val}");
                        else if (val >= -63 && val < 0)
                            Emit("ADIW", "R24", $"{-val}");
                        else { Emit("SUBI", "R24", $"{(byte)(val & 0xFF)}"); Emit("SBCI", "R25", $"{(byte)((val >> 8) & 0xFF)}"); }
                        usedImm = true;
                        break;
                }
            }
        }

        if (!usedImm) LoadIntoReg(b.Src2, "R18", type);

        switch (b.Op)
        {
            case IrBinOp.Add:
                if (!usedImm)
                {
                    Emit("ADD", "R24", "R18");
                    if (is16 || is32) Emit("ADC", "R25", "R19");
                    if (is32) { Emit("ADC", "R22", "R20"); Emit("ADC", "R23", "R21"); }
                }

                break;
            case IrBinOp.Sub:
                if (!usedImm)
                {
                    Emit("SUB", "R24", "R18");
                    if (is16 || is32) Emit("SBC", "R25", "R19");
                    if (is32) { Emit("SBC", "R22", "R20"); Emit("SBC", "R23", "R21"); }
                }

                break;
            case IrBinOp.BitAnd:
                if (!usedImm)
                {
                    Emit("AND", "R24", "R18");
                    if (is16 || is32) Emit("AND", "R25", "R19");
                    if (is32) { Emit("AND", "R22", "R20"); Emit("AND", "R23", "R21"); }
                }

                break;
            case IrBinOp.BitOr:
                if (!usedImm)
                {
                    Emit("OR", "R24", "R18");
                    if (is16 || is32) Emit("OR", "R25", "R19");
                    if (is32) { Emit("OR", "R22", "R20"); Emit("OR", "R23", "R21"); }
                }

                break;
            case IrBinOp.BitXor:
                Emit("EOR", "R24", "R18");
                if (is16 || is32) Emit("EOR", "R25", "R19");
                if (is32) { Emit("EOR", "R22", "R20"); Emit("EOR", "R23", "R21"); }
                break;
            case IrBinOp.LShift:
                if (!usedImm)
                {
                    var ls = MakeLabel("L_SHIFT_START");
                    var ld = MakeLabel("L_SHIFT_DONE");
                    EmitLabel(ls);
                    Emit("TST", "R18");
                    EmitBranch("BREQ", ld);
                    Emit("LSL", "R24");
                    if (is16 || is32) Emit("ROL", "R25");
                    if (is32) { Emit("ROL", "R22"); Emit("ROL", "R23"); }
                    Emit("DEC", "R18");
                    Emit("RJMP", ls);
                    EmitLabel(ld);
                }

                break;
            case IrBinOp.RShift:
                if (!usedImm)
                {
                    var rs = MakeLabel("L_SHIFT_START");
                    var rd = MakeLabel("L_SHIFT_DONE");
                    EmitLabel(rs);
                    Emit("TST", "R18");
                    EmitBranch("BREQ", rd);
                    if (is32)
                    {
                        if (IsSignedType(type)) Emit("ASR", "R23"); else Emit("LSR", "R23");
                        Emit("ROR", "R22");
                        Emit("ROR", "R25");
                        Emit("ROR", "R24");
                    }
                    else if (is16)
                    {
                        if (IsSignedType(type)) Emit("ASR", "R25"); else Emit("LSR", "R25");
                        Emit("ROR", "R24");
                    }
                    else
                    {
                        if (IsSignedType(type)) Emit("ASR", "R24"); else Emit("LSR", "R24");
                    }

                    Emit("DEC", "R18");
                    Emit("RJMP", rs);
                    EmitLabel(rd);
                }

                break;
            case IrBinOp.Mul:
                if (is16)
                {
                    // 16x16 -> 16-bit product (low 16 bits only).
                    // a = R25:R24 (hi:lo), b = R19:R18 (hi:lo).
                    if (IsSignedType(type))
                    {
                        // Signed path: MULSU requires both operands in R16-R23.
                        // R24/R25 are outside that range, so copy them to R22/R23.
                        // R22 = a_hi (copy of R25), R23 = a_lo (copy of R24).
                        Emit("MUL",   "R24", "R18");  // unsigned lo×lo -> R1:R0
                        Emit("MOV",   "R20", "R0");   // result_lo
                        Emit("MOV",   "R21", "R1");   // partial_hi
                        Emit("MOV",   "R22", "R25");  // a_hi -> R22 (within R16-R23)
                        Emit("MULSU", "R22", "R18");  // signed(a_hi) × unsigned(b_lo) -> R1:R0
                        Emit("ADD",   "R21", "R0");   // partial_hi += R0
                        Emit("MOV",   "R23", "R24");  // a_lo -> R23 (within R16-R23)
                        Emit("MULSU", "R19", "R23");  // signed(b_hi) × unsigned(a_lo) -> R1:R0
                        Emit("ADD",   "R21", "R0");   // partial_hi += R0
                        Emit("MOV",   "R24", "R20");
                        Emit("MOV",   "R25", "R21");
                    }
                    else
                    {
                        // Unsigned path: all MUL (unsigned × unsigned).
                        Emit("MUL", "R24", "R18");  // a_lo * b_lo -> R1:R0
                        Emit("MOV", "R20", "R0");   // result_lo
                        Emit("MOV", "R21", "R1");   // result_hi (partial)
                        Emit("MUL", "R24", "R19");  // a_lo * b_hi -> R1:R0
                        Emit("ADD", "R21", "R0");   // result_hi += low(a_lo*b_hi)
                        Emit("MUL", "R25", "R18");  // a_hi * b_lo -> R1:R0
                        Emit("ADD", "R21", "R0");   // result_hi += low(a_hi*b_lo)
                        Emit("MOV", "R24", "R20");
                        Emit("MOV", "R25", "R21");
                    }
                }
                else
                {
                    Emit("MUL", "R24", "R18");
                    Emit("MOV", "R24", "R0");
                }
                Emit("CLR", "R1");
                break;
            case IrBinOp.Div:
            case IrBinOp.FloorDiv:
                if (is32) Emit("CALL", "__div32");
                else if (is16) Emit("CALL", "__div16");
                else Emit("CALL", "__div8");
                break;
            case IrBinOp.Mod:
                if (is32) Emit("CALL", "__mod32");
                else if (is16) Emit("CALL", "__mod16");
                else Emit("CALL", "__mod8");
                break;
            case IrBinOp.Equal:
            {
                if (!usedImm)
                {
                    Emit("CP", "R24", "R18");
                    if (is16) Emit("CPC", "R25", "R19");
                }

                var sk = MakeLabel("L_SKIP");
                Emit("LDI", "R24", "1");
                EmitBranch("BREQ", sk);
                Emit("LDI", "R24", "0");
                EmitLabel(sk);
                if (is16) Emit("LDI", "R25", "0");
                break;
            }
            case IrBinOp.NotEqual:
            {
                if (!usedImm)
                {
                    Emit("CP", "R24", "R18");
                    if (is16) Emit("CPC", "R25", "R19");
                }

                var sk = MakeLabel("L_SKIP");
                Emit("LDI", "R24", "1");
                EmitBranch("BRNE", sk);
                Emit("LDI", "R24", "0");
                EmitLabel(sk);
                if (is16) Emit("LDI", "R25", "0");
                break;
            }
            case IrBinOp.LessThan:
            {
                if (!usedImm)
                {
                    Emit("CP", "R24", "R18");
                    if (is16) Emit("CPC", "R25", "R19");
                }

                var sk = MakeLabel("L_SKIP");
                Emit("LDI", "R24", "1");
                EmitBranch(IsSignedComparison(b.Src1, b.Src2) ? "BRLT" : "BRLO", sk);
                Emit("LDI", "R24", "0");
                EmitLabel(sk);
                if (is16) Emit("LDI", "R25", "0");
                break;
            }
            case IrBinOp.GreaterEqual:
            {
                if (!usedImm)
                {
                    Emit("CP", "R24", "R18");
                    if (is16) Emit("CPC", "R25", "R19");
                }

                var sk = MakeLabel("L_SKIP");
                Emit("LDI", "R24", "1");
                EmitBranch(IsSignedComparison(b.Src1, b.Src2) ? "BRGE" : "BRSH", sk);
                Emit("LDI", "R24", "0");
                EmitLabel(sk);
                if (is16) Emit("LDI", "R25", "0");
                break;
            }
            case IrBinOp.GreaterThan:
            {
                if (!usedImm)
                {
                    Emit("CP", "R24", "R18");
                    if (is16) Emit("CPC", "R25", "R19");
                }

                var lt = MakeLabel("L_TRUE");
                var ld2 = MakeLabel("L_DONE");
                EmitBranch("BREQ", ld2);
                EmitBranch(IsSignedComparison(b.Src1, b.Src2) ? "BRGE" : "BRSH", lt);
                EmitLabel(ld2);
                Emit("LDI", "R24", "0");
                var lf = MakeLabel("L_FINAL");
                Emit("RJMP", lf);
                EmitLabel(lt);
                Emit("LDI", "R24", "1");
                EmitLabel(lf);
                if (is16) Emit("LDI", "R25", "0");
                break;
            }
            case IrBinOp.LessEqual:
            {
                if (!usedImm)
                {
                    Emit("CP", "R24", "R18");
                    if (is16) Emit("CPC", "R25", "R19");
                }

                var lt = MakeLabel("L_TRUE");
                EmitBranch(IsSignedComparison(b.Src1, b.Src2) ? "BRLT" : "BRLO", lt);
                EmitBranch("BREQ", lt);
                Emit("LDI", "R24", "0");
                var lf = MakeLabel("L_FINAL");
                Emit("RJMP", lf);
                EmitLabel(lt);
                Emit("LDI", "R24", "1");
                EmitLabel(lf);
                if (is16) Emit("LDI", "R25", "0");
                break;
            }
        }

        StoreRegInto("R24", b.Dst, type);
    }

    private void CompileBitSet(BitSet bs)
    {
        if (bs.Target is MemoryAddress { Address: >= 0x20 and <= 0x3F } mem)
        {
            Emit("SBI", $"0x{mem.Address - 0x20:X2}", $"{bs.Bit}");
            return;
        }

        LoadIntoReg(bs.Target, "R24");
        Emit("ORI", "R24", $"{1 << bs.Bit}");
        StoreRegInto("R24", bs.Target);
    }

    private void CompileBitClear(BitClear bc)
    {
        if (bc.Target is MemoryAddress { Address: >= 0x20 and <= 0x3F } mem)
        {
            Emit("CBI", $"0x{mem.Address - 0x20:X2}", $"{bc.Bit}");
            return;
        }

        LoadIntoReg(bc.Target, "R24");
        Emit("ANDI", "R24", $"{(byte)~(1 << bc.Bit)}");
        StoreRegInto("R24", bc.Target);
    }

    private void CompileBitCheck(BitCheck bck)
    {
        if (bck.Source is MemoryAddress { Address: >= 0x20 and <= 0x3F } mem)
        {
            var lF = MakeLabel("L_BIT_FALSE");
            var lD = MakeLabel("L_BIT_DONE");
            Emit("SBIS", $"0x{mem.Address - 0x20:X2}", $"{bck.Bit}");
            Emit("RJMP", lF);
            Emit("LDI", "R24", "1");
            Emit("RJMP", lD);
            EmitLabel(lF);
            Emit("LDI", "R24", "0");
            EmitLabel(lD);
            StoreRegInto("R24", bck.Dst);
            return;
        }

        LoadIntoReg(bck.Source, "R24");
        Emit("ANDI", "R24", $"{1 << bck.Bit}");
        var sk = MakeLabel("L_SKIP");
        Emit("LDI", "R18", "1");
        EmitBranch("BRNE", sk);
        Emit("LDI", "R18", "0");
        EmitLabel(sk);
        StoreRegInto("R18", bck.Dst);
    }

    private void CompileBitWrite(BitWrite bw)
    {
        if (bw.Target is MemoryAddress { Address: >= 0x20 and <= 0x3F } mem)
        {
            LoadIntoReg(bw.Src, "R24");
            var sk = MakeLabel("L_BIT_WRITE_SKIP");
            var dn = MakeLabel("L_BIT_WRITE_DONE");
            Emit("TST", "R24");
            EmitBranch("BREQ", sk);
            Emit("SBI", $"0x{mem.Address - 0x20:X2}", $"{bw.Bit}");
            Emit("RJMP", dn);
            EmitLabel(sk);
            Emit("CBI", $"0x{mem.Address - 0x20:X2}", $"{bw.Bit}");
            EmitLabel(dn);
            return;
        }

        LoadIntoReg(bw.Src, "R24");
        LoadIntoReg(bw.Target, "R18");
        var sk2 = MakeLabel("L_BIT_WRITE_SKIP");
        var dn2 = MakeLabel("L_BIT_WRITE_DONE");
        Emit("TST", "R24");
        EmitBranch("BREQ", sk2);
        Emit("ORI", "R18", $"{1 << bw.Bit}");
        Emit("RJMP", dn2);
        EmitLabel(sk2);
        Emit("ANDI", "R18", $"{(byte)~(1 << bw.Bit)}");
        EmitLabel(dn2);
        StoreRegInto("R18", bw.Target);
    }

    private void CompileJumpIfBitSet(JumpIfBitSet jbs)
    {
        if (jbs.Source is MemoryAddress { Address: >= 0x20 and <= 0x3F } mem)
        {
            Emit("SBIC", $"0x{mem.Address - 0x20:X2}", $"{jbs.Bit}");
            Emit("RJMP", jbs.Target);
            return;
        }

        LoadIntoReg(jbs.Source, "R24");
        Emit("ANDI", "R24", $"{1 << jbs.Bit}");
        EmitBranch("BRNE", jbs.Target);
    }

    private void CompileJumpIfBitClear(JumpIfBitClear jbc)
    {
        if (jbc.Source is MemoryAddress { Address: >= 0x20 and <= 0x3F } mem)
        {
            Emit("SBIS", $"0x{mem.Address - 0x20:X2}", $"{jbc.Bit}");
            Emit("RJMP", jbc.Target);
            return;
        }

        LoadIntoReg(jbc.Source, "R24");
        Emit("ANDI", "R24", $"{1 << jbc.Bit}");
        EmitBranch("BREQ", jbc.Target);
    }

    private void CompileAugAssign(AugAssign aa)
    {
        var type = GetValType(aa.Target);
        var is16 = type.SizeOf() == 2;
        var is32 = type.SizeOf() == 4;
        LoadIntoReg(aa.Target, "R24", type);

        var usedImm = false;
        if (aa.Operand is Constant c)
        {
            var val = c.Value;
            if (!is16)
            {
                switch (aa.Op)
                {
                    case IrBinOp.Add:
                        if (val == 1) Emit("INC", "R24");
                        else if (val == 255) Emit("DEC", "R24");
                        else Emit("SUBI", "R24", $"{(byte)(-val)}");
                        usedImm = true;
                        break;
                    case IrBinOp.Sub:
                        if (val == 1) Emit("DEC", "R24");
                        else if (val == 255) Emit("INC", "R24");
                        else Emit("SUBI", "R24", $"{val & 0xFF}");
                        usedImm = true;
                        break;
                    case IrBinOp.BitAnd:
                        Emit("ANDI", "R24", $"{val & 0xFF}");
                        usedImm = true;
                        break;
                    case IrBinOp.BitOr:
                        Emit("ORI", "R24", $"{val & 0xFF}");
                        usedImm = true;
                        break;
                    case IrBinOp.BitXor:
                        Emit("LDI", "R18", $"{val & 0xFF}");
                        Emit("EOR", "R24", "R18");
                        usedImm = true;
                        break;
                    case IrBinOp.LShift:
                        for (int i = 0; i < (val & 7); i++) Emit("LSL", "R24");
                        usedImm = true;
                        break;
                    case IrBinOp.RShift:
                        for (int i = 0; i < (val & 7); i++)
                            if (IsSignedType(type)) Emit("ASR", "R24"); else Emit("LSR", "R24");
                        usedImm = true;
                        break;
                    case IrBinOp.Mul:
                    case IrBinOp.Div:
                    case IrBinOp.FloorDiv:
                    case IrBinOp.Mod:
                    case IrBinOp.Equal:
                    case IrBinOp.NotEqual:
                    case IrBinOp.LessThan:
                    case IrBinOp.LessEqual:
                    case IrBinOp.GreaterThan:
                    case IrBinOp.GreaterEqual:
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }
            else
            {
                switch (aa.Op)
                {
                    case IrBinOp.Add:
                        if (val >= 0 && val <= 63)
                            Emit("ADIW", "R24", $"{val}");
                        else if (val >= -63 && val < 0)
                            Emit("SBIW", "R24", $"{-val}");
                        else { var neg = -val; Emit("SUBI", "R24", $"{(byte)(neg & 0xFF)}"); Emit("SBCI", "R25", $"{(byte)((neg >> 8) & 0xFF)}"); }
                        usedImm = true;
                        break;
                    case IrBinOp.Sub:
                        if (val >= 0 && val <= 63)
                            Emit("SBIW", "R24", $"{val}");
                        else if (val >= -63 && val < 0)
                            Emit("ADIW", "R24", $"{-val}");
                        else { Emit("SUBI", "R24", $"{(byte)(val & 0xFF)}"); Emit("SBCI", "R25", $"{(byte)((val >> 8) & 0xFF)}"); }
                        usedImm = true;
                        break;
                    default:
                        Emit("LDI", "R18", $"{val & 0xFF}");
                        Emit("LDI", "R19", $"{(val >> 8) & 0xFF}");
                        break;
                }
            }
        }

        if (!usedImm) LoadIntoReg(aa.Operand, "R18", type);

        if (!usedImm)
        {
            switch (aa.Op)
            {
                case IrBinOp.Add:
                    Emit("ADD", "R24", "R18");
                    if (is16)
                        Emit("ADC", "R25", "R19");
                    break;
                case IrBinOp.Sub:
                    Emit("SUB", "R24", "R18");
                    if (is16) Emit("SBC", "R25", "R19");
                    break;
                case IrBinOp.BitAnd:
                    Emit("AND", "R24", "R18");
                    if (is16) Emit("AND", "R25", "R19");
                    break;
                case IrBinOp.BitOr:
                    Emit("OR", "R24", "R18");
                    if (is16) Emit("OR", "R25", "R19");
                    break;
                case IrBinOp.BitXor:
                    Emit("EOR", "R24", "R18");
                    if (is16) Emit("EOR", "R25", "R19");
                    break;
                case IrBinOp.LShift:
                {
                    var ls = MakeLabel("L_AUG_LSHIFT");
                    var ld = MakeLabel("L_AUG_LSHIFT_DONE");
                    EmitLabel(ls);
                    Emit("TST", "R18");
                    EmitBranch("BREQ", ld);
                    Emit("LSL", "R24");
                    if (is16) Emit("ROL", "R25");
                    Emit("DEC", "R18");
                    Emit("RJMP", ls);
                    EmitLabel(ld);
                    break;
                }
                case IrBinOp.RShift:
                {
                    var rs = MakeLabel("L_AUG_RSHIFT");
                    var rd = MakeLabel("L_AUG_RSHIFT_DONE");
                    EmitLabel(rs);
                    Emit("TST", "R18");
                    EmitBranch("BREQ", rd);
                    if (is16)
                    {
                        if (IsSignedType(type)) Emit("ASR", "R25"); else Emit("LSR", "R25");
                        Emit("ROR", "R24");
                    }
                    else
                    {
                        if (IsSignedType(type)) Emit("ASR", "R24"); else Emit("LSR", "R24");
                    }

                    Emit("DEC", "R18");
                    Emit("RJMP", rs);
                    EmitLabel(rd);
                    break;
                }
                case IrBinOp.Mul:
                    if (is16)
                    {
                        // a = R25:R24 (hi:lo), b = R19:R18 (hi:lo).
                        if (IsSignedType(type))
                        {
                            // Signed: MULSU requires operands in R16-R23.
                            // Copy a_hi/a_lo into R22/R23 (within range).
                            Emit("MUL",   "R24", "R18");  // unsigned lo×lo -> R1:R0
                            Emit("MOV",   "R20", "R0");
                            Emit("MOV",   "R21", "R1");
                            Emit("MOV",   "R22", "R25");  // a_hi -> R22
                            Emit("MULSU", "R22", "R18");  // signed(a_hi) × unsigned(b_lo)
                            Emit("ADD",   "R21", "R0");
                            Emit("MOV",   "R23", "R24");  // a_lo -> R23
                            Emit("MULSU", "R19", "R23");  // signed(b_hi) × unsigned(a_lo)
                            Emit("ADD",   "R21", "R0");
                            Emit("MOV",   "R24", "R20");
                            Emit("MOV",   "R25", "R21");
                        }
                        else
                        {
                            Emit("MUL", "R24", "R18");
                            Emit("MOV", "R20", "R0");
                            Emit("MOV", "R21", "R1");
                            Emit("MUL", "R24", "R19");
                            Emit("ADD", "R21", "R0");
                            Emit("MUL", "R25", "R18");
                            Emit("ADD", "R21", "R0");
                            Emit("MOV", "R24", "R20");
                            Emit("MOV", "R25", "R21");
                        }
                    }
                    else
                    {
                        Emit("MUL", "R24", "R18");
                        Emit("MOV", "R24", "R0");
                    }
                    Emit("CLR", "R1");
                    break;
                case IrBinOp.Div:
                case IrBinOp.FloorDiv:
                    if (is32) Emit("CALL", "__div32");
                    else if (is16) Emit("CALL", "__div16");
                    else Emit("CALL", "__div8");
                    break;
                case IrBinOp.Mod:
                    if (is32) Emit("CALL", "__mod32");
                    else if (is16) Emit("CALL", "__mod16");
                    else Emit("CALL", "__mod8");
                    break;
                case IrBinOp.Equal:
                case IrBinOp.NotEqual:
                case IrBinOp.LessThan:
                case IrBinOp.LessEqual:
                case IrBinOp.GreaterThan:
                case IrBinOp.GreaterEqual:
                default: throw new Exception($"AugAssign op {aa.Op} not implemented in AVR backend");
            }
        }

        StoreRegInto("R24", aa.Target, type);
    }

    private void CompileArrayLoad(ArrayLoad al)
    {
        var elemSize = al.ElemType.SizeOf();
        var is16 = elemSize == 2;
        if (!_stackLayout.TryGetValue(al.ArrayName, out int baseOffset))
        {
            EmitComment("ArrayLoad: array not in stack_layout -- skip");
            return;
        }

        if (al.Index is Constant c)
        {
            var offset = baseOffset + c.Value * elemSize;
            if (offset < 64)
            {
                Emit("LDD", "R24", $"Y+{offset}");
                if (is16) Emit("LDD", "R25", $"Y+{offset + 1}");
            }
            else
            {
                Emit("LDS", "R24", $"0x{0x0100 + offset:X4}");
                if (is16) Emit("LDS", "R25", $"0x{0x0100 + offset + 1:X4}");
            }
        }
        else
        {
            EmitComment("ArrayLoad variable index via Z");
            LoadIntoReg(al.Index, "R24");
            if (elemSize == 2) Emit("LSL", "R24");
            var absBase = 0x0100 + baseOffset;
            Emit("LDI", "R30", $"low({absBase})");
            Emit("LDI", "R31", $"high({absBase})");
            Emit("CLR", "R16"); // R16 = 0 (Clears carry, but we don't care yet)
            Emit("ADD", "R30", "R24"); // Add offset to Z low byte (Generates carry if overflow)
            Emit("ADC", "R31", "R16"); // Add 0 + carry to Z high byte
            Emit("LD", "R24", "Z");
            if (is16) Emit("LDD", "R25", "Z+1");
        }

        StoreRegInto("R24", al.Dst, al.ElemType);
    }

    private void CompileArrayStore(ArrayStore ast)
    {
        var elemSize = ast.ElemType.SizeOf();
        var is16 = elemSize == 2;
        if (!_stackLayout.TryGetValue(ast.ArrayName, out int baseOffset))
        {
            EmitComment("ArrayStore: array not in stack_layout -- skip");
            return;
        }

        LoadIntoReg(ast.Src, "R24", ast.ElemType);

        if (ast.Index is Constant c)
        {
            var offset = baseOffset + c.Value * elemSize;
            if (offset < 64)
            {
                Emit("STD", $"Y+{offset}", "R24");
                if (is16) Emit("STD", $"Y+{offset + 1}", "R25");
            }
            else
            {
                Emit("STS", $"0x{0x0100 + offset:X4}", "R24");
                if (is16) Emit("STS", $"0x{0x0100 + offset + 1:X4}", "R25");
            }
        }
        else
        {
            Emit("MOV", "R18", "R24");
            if (is16) Emit("MOV", "R19", "R25");
            EmitComment("ArrayStore variable index via Z");
            LoadIntoReg(ast.Index, "R24");
            if (elemSize == 2) Emit("LSL", "R24");
            var absBase = 0x0100 + baseOffset;
            Emit("LDI", "R30", $"low({absBase})");
            Emit("LDI", "R31", $"high({absBase})");
            Emit("CLR", "R16"); // R16 = 0
            Emit("ADD", "R30", "R24"); // Z_low = Z_low + offset (Sets Carry if overflow)
            Emit("ADC", "R31", "R16"); // Z_high = Z_high + 0 + Carry
            Emit("ST", "Z", "R18");
            if (is16) Emit("STD", "Z+1", "R19");
        }
    }

    private string InternString(string text)
    {
        if (_stringPool.TryGetValue(text, out var label)) return label;
        label = $"__str_{_stringPool.Count}";
        _stringPool[text] = label;
        return label;
    }

    private void CompileUartSendString(UARTSendString us)
    {
        _uartSendZNeeded = true;
        var content = us.Text + us.EndStr;
        if (string.IsNullOrEmpty(content)) return;
        var label = InternString(content);

        Emit("LDI", "R30", $"lo8({label})");
        Emit("LDI", "R31", $"hi8({label})");
        Emit("CALL", "__uart_send_z");
    }

    private void EmitStringPool(TextWriter os)
    {
        if (!_uartSendZNeeded) return;
        os.WriteLine();
        os.WriteLine("; --- Flash String Pool (LPM+Z UART send) ---");
        os.WriteLine("__uart_send_z:");
        os.WriteLine("__usendz_loop:");
        os.WriteLine("\tLPM\tR24, Z+");
        os.WriteLine("\tTST\tR24");
        os.WriteLine("\tBREQ\t__usendz_done");
        os.WriteLine("__usendz_wait:");
        os.WriteLine("\tLDS\tR25, 0x00C0");
        os.WriteLine("\tSBRS\tR25, 5");
        os.WriteLine("\tRJMP\t__usendz_wait");
        os.WriteLine("\tSTS\t0x00C6, R24");
        os.WriteLine("\tRJMP\t__usendz_loop");
        os.WriteLine("__usendz_done:");
        os.WriteLine("\tRET");
        os.WriteLine();

        foreach (var (text, label) in _stringPool)
        {
            os.WriteLine($"{label}:");
            os.Write("\t.byte ");
            var first = true;
            foreach (var ch in System.Text.Encoding.ASCII.GetBytes(text))
            {
                if (!first) os.Write(", ");
                os.Write(ch);
                first = false;
            }

            os.WriteLine(", 0");
            os.WriteLine("\t.balign 2");
        }
    }
}