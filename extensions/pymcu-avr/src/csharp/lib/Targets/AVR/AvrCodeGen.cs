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

using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using PyMCU.Backend.Analysis;
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
    private readonly HashSet<int> _usedExnCodes = [];
    private HashSet<string> _varIsFloat = new();
    private readonly Dictionary<string, List<int>> _flashArrayPool = new();
    // Maps function name → list of parameter sizes (in bytes) for correct call-site arg loading.
    private Dictionary<string, List<int>> _functionParamSizes = new();
    private int _labelCounter;
    private Function? _currentFunction;
    private int _maxStaticUsage; // total static SRAM used by StackAllocator; set in Compile()
    private bool _needsGc;      // mirrors program.NeedsGc for use in CompileFunction

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
        FloatConstant => DataType.FLOAT,
        Variable v => v.Type,
        Temporary t => t.Type,
        MemoryAddress m => m.Type.SizeOf() > 1 ? m.Type : DataType.UINT8,
        FunctionRef => DataType.UINT16,  // Function word address is 2 bytes
        Constant { Value: > 255 or < -128 } => DataType.UINT16,
        _ => DataType.UINT8,
    };

    // -------------------------------------------------------------------------
    // Soft-float helpers
    // -------------------------------------------------------------------------

    // Load a FLOAT value into R22(B0/LSB):R23(B1):R24(B2):R25(B3/MSB).
    private void LoadFloatIntoRegs(Val val)
    {
        if (val is FloatConstant fc)
        {
            uint bits = BitConverter.SingleToUInt32Bits((float)fc.Value);
            Emit("LDI", "R22", $"{bits & 0xFF}");
            Emit("LDI", "R23", $"{(bits >> 8) & 0xFF}");
            Emit("LDI", "R24", $"{(bits >> 16) & 0xFF}");
            Emit("LDI", "R25", $"{(bits >> 24) & 0xFF}");
            return;
        }

        // If the source is an integer type, load it as int and convert to float.
        DataType srcType = GetValType(val);
        if (srcType != DataType.FLOAT)
        {
            // GCC __floatsisf: input int32 in R25:R24:R23:R22 (R22=LSB), result float in R25:R24:R23:R22.
            if (srcType.SizeOf() == 1)
            {
                LoadIntoReg(val, "R22", DataType.UINT8);
                if (IsSignedType(srcType))
                {
                    Emit("MOV", "R23", "R22");
                    Emit("LSL", "R23");
                    Emit("SBC", "R23", "R23"); // sign-extend: R23 = 0xFF if negative, 0x00 if positive
                    Emit("MOV", "R24", "R23");
                    Emit("MOV", "R25", "R23");
                }
                else
                {
                    Emit("CLR", "R23");
                    Emit("CLR", "R24");
                    Emit("CLR", "R25");
                }
            }
            else
            {
                // uint16 or int16: load into R22 (lo) : R23 (hi), sign/zero-extend R24:R25.
                LoadIntoReg(val, "R22", DataType.UINT16);
                if (IsSignedType(srcType))
                {
                    Emit("MOV", "R24", "R23");
                    Emit("LSL", "R24");
                    Emit("SBC", "R24", "R24"); // sign-extend: R24 = 0xFF if negative, 0x00 if positive
                    Emit("MOV", "R25", "R24");
                }
                else
                {
                    Emit("CLR", "R24");
                    Emit("CLR", "R25");
                }
            }
            Emit("CALL", "__floatsisf");
            return;
        }

        var name = val switch { Variable v => v.Name, Temporary t => t.Name, _ => "" };

        // If the variable is register-allocated, load from those registers.
        if (!string.IsNullOrEmpty(name) && _regLayout.TryGetValue(name, out string? regBase))
        {
            int rn = int.Parse(regBase[1..]);
            Emit("MOV", "R22", $"R{rn}");
            Emit("MOV", "R23", $"R{rn + 1}");
            Emit("MOV", "R24", $"R{rn + 2}");
            Emit("MOV", "R25", $"R{rn + 3}");
            return;
        }

        if (!string.IsNullOrEmpty(name) && _stackLayout.TryGetValue(name, out int offset))
        {
            if (offset + 3 < 64)
            {
                Emit("LDD", "R22", $"Y+{offset}");
                Emit("LDD", "R23", $"Y+{offset + 1}");
                Emit("LDD", "R24", $"Y+{offset + 2}");
                Emit("LDD", "R25", $"Y+{offset + 3}");
            }
            else
            {
                int abs = 0x0100 + offset;
                Emit("LDS", "R22", $"0x{abs:X4}");
                Emit("LDS", "R23", $"0x{abs + 1:X4}");
                Emit("LDS", "R24", $"0x{abs + 2:X4}");
                Emit("LDS", "R25", $"0x{abs + 3:X4}");
            }
            return;
        }

        // Global variable fallback
        var addr = ResolveAddress(val);
        Emit("LDS", "R22", addr);
        Emit("LDS", "R23", $"{addr}+1");
        Emit("LDS", "R24", $"{addr}+2");
        Emit("LDS", "R25", $"{addr}+3");
    }

    // Store R22:R23:R24:R25 into a FLOAT destination.
    private void StoreFloatFromRegs(Val dst)
    {
        var name = dst switch { Variable v => v.Name, Temporary t => t.Name, _ => "" };

        // If the destination is register-allocated, move into those registers.
        if (!string.IsNullOrEmpty(name) && _regLayout.TryGetValue(name, out string? regBase))
        {
            int rn = int.Parse(regBase[1..]);
            Emit("MOV", $"R{rn}", "R22");
            Emit("MOV", $"R{rn + 1}", "R23");
            Emit("MOV", $"R{rn + 2}", "R24");
            Emit("MOV", $"R{rn + 3}", "R25");
            return;
        }

        if (!string.IsNullOrEmpty(name) && _stackLayout.TryGetValue(name, out int offset))
        {
            if (offset + 3 < 64)
            {
                Emit("STD", $"Y+{offset}", "R22");
                Emit("STD", $"Y+{offset + 1}", "R23");
                Emit("STD", $"Y+{offset + 2}", "R24");
                Emit("STD", $"Y+{offset + 3}", "R25");
            }
            else
            {
                int abs = 0x0100 + offset;
                Emit("STS", $"0x{abs:X4}", "R22");
                Emit("STS", $"0x{abs + 1:X4}", "R23");
                Emit("STS", $"0x{abs + 2:X4}", "R24");
                Emit("STS", $"0x{abs + 3:X4}", "R25");
            }
            return;
        }

        var addr = ResolveAddress(dst);
        Emit("STS", addr, "R22");
        Emit("STS", $"{addr}+1", "R23");
        Emit("STS", $"{addr}+2", "R24");
        Emit("STS", $"{addr}+3", "R25");
    }

    // Compile a binary operation where at least one operand is FLOAT.
    private void CompileFloatBinary(Binary b)
    {
        // Load arg0 (Src1) into R22:R25, push onto stack, load arg1 (Src2) into R22:R25,
        // move arg1 to R18:R21 (GCC arg1 slot), restore arg0 to R22:R25 (GCC arg0 slot).
        // GCC AVR float ABI: arg0 in R25:R24:R23:R22, arg1 in R21:R20:R19:R18, result in R25:R24:R23:R22.
        LoadFloatIntoRegs(b.Src1);
        Emit("PUSH", "R25");
        Emit("PUSH", "R24");
        Emit("PUSH", "R23");
        Emit("PUSH", "R22");
        LoadFloatIntoRegs(b.Src2);
        Emit("MOV", "R18", "R22");
        Emit("MOV", "R19", "R23");
        Emit("MOV", "R20", "R24");
        Emit("MOV", "R21", "R25");
        Emit("POP", "R22");
        Emit("POP", "R23");
        Emit("POP", "R24");
        Emit("POP", "R25");

        bool isArith = b.Op is IrBinOp.Add or IrBinOp.Sub or IrBinOp.Mul
                       or IrBinOp.Div or IrBinOp.FloorDiv;
        if (isArith)
        {
            string routine = b.Op switch
            {
                IrBinOp.Add                     => "__addsf3",
                IrBinOp.Sub                     => "__subsf3",
                IrBinOp.Mul                     => "__mulsf3",
                IrBinOp.Div or IrBinOp.FloorDiv => "__divsf3",
                _ => throw new NotSupportedException($"Float arith op {b.Op} not supported")
            };
            Emit("CALL", routine);
            var dstType = GetValType(b.Dst);
            if (dstType == DataType.FLOAT)
                StoreFloatFromRegs(b.Dst);
            else
            {
                // Result is float in R22:R25; __fixsfsi converts to int32 (R22=LSB).
                Emit("CALL", "__fixsfsi");
                Emit("MOV", "R24", "R22");
                Emit("MOV", "R25", "R23");
                StoreRegInto("R24", b.Dst, dstType);
            }
        }
        else
        {
            // Float comparison via GCC __cmpsf2.
            // Returns in R24: 0xFF if arg0<arg1, 0x00 if arg0==arg1, 0x01 if arg0>arg1.
            Emit("CALL", "__cmpsf2");
            string trueLabel = MakeLabel("L_FCMP_T");
            string doneLabel = MakeLabel("L_FCMP_D");
            switch (b.Op)
            {
                case IrBinOp.Equal:        // true when R24 == 0x00
                    Emit("CPI", "R24", "0x00"); Emit("BREQ", trueLabel); break;
                case IrBinOp.NotEqual:     // true when R24 != 0x00
                    Emit("CPI", "R24", "0x00"); Emit("BRNE", trueLabel); break;
                case IrBinOp.LessThan:     // true when R24 == 0xFF (a < b)
                    Emit("CPI", "R24", "0xFF"); Emit("BREQ", trueLabel); break;
                case IrBinOp.LessEqual:    // true when R24 != 0x01 (covers 0x00 and 0xFF)
                    Emit("CPI", "R24", "0x01"); Emit("BRNE", trueLabel); break;
                case IrBinOp.GreaterThan:  // true when R24 == 0x01 (a > b)
                    Emit("CPI", "R24", "0x01"); Emit("BREQ", trueLabel); break;
                case IrBinOp.GreaterEqual: // true when R24 != 0xFF (covers 0x00 and 0x01)
                    Emit("CPI", "R24", "0xFF"); Emit("BRNE", trueLabel); break;
                default: throw new NotSupportedException($"Float comparison op {b.Op} not supported");
            }
            Emit("CLR", "R24");
            Emit("RJMP", doneLabel);
            EmitLabel(trueLabel);
            Emit("LDI", "R24", "1");
            EmitLabel(doneLabel);
            StoreRegInto("R24", b.Dst, DataType.UINT8);
        }
    }

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
            { "BRTS", "BRTC" }, { "BRTC", "BRTS" },  // T-flag branches (error propagation)
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
            case ArrayBase ab:
            {
                string regH2 = GetHighReg(reg);
                // Try the array base name first (registered via ArrayStore), then fall back
                // to the first element "__0" (registered via individual Copy instructions).
                int abOffset;
                bool found = _stackLayout.TryGetValue(ab.ArrayName, out abOffset)
                          || _stackLayout.TryGetValue(ab.ArrayName + "__0", out abOffset);
                if (found) {
                    int absAddr = 0x0100 + abOffset;
                    Emit("LDI", reg,   $"lo8(0x{absAddr:X4})");
                    Emit("LDI", regH2, $"hi8(0x{absAddr:X4})");
                } else {
                    string label = ab.ArrayName.Replace('.', '_');
                    Emit("LDI", reg,   $"lo8({label})");
                    Emit("LDI", regH2, $"hi8({label})");
                }
                return;
            }
            case Constant c:
            {
                Emit("LDI", reg, $"{c.Value & 0xFF}");
                if (size >= 2) Emit("LDI", regH, $"{(c.Value >> 8) & 0xFF}");
                if (size == 4) { Emit("LDI", regB2, $"{(c.Value >> 16) & 0xFF}"); Emit("LDI", regB3, $"{(c.Value >> 24) & 0xFF}"); }
                return;
            }
            case FunctionRef fr:
            {
                // Load function word address (gs() modifier) into register pair.
                Emit("LDI", reg, $"lo8(gs({fr.FunctionName}))");
                if (size >= 2) Emit("LDI", regH, $"hi8(gs({fr.FunctionName}))");
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

        // "__exn_r22_capture" is a register alias: it represents R22 at catch-dispatcher
        // entry — the physical register where SignalError deposited the error code.
        // Compiled as MOV reg, R22 (zero SRAM cost, no stack-overlay risk).
        if (name == "__exn_r22_capture")
        {
            if (reg != "R22") Emit("MOV", reg, "R22");
            return;
        }

        if (!string.IsNullOrEmpty(name) && _regLayout.TryGetValue(name, out var srcReg))
        {
            DataType sourceType = GetValType(val);
            bool needSignExt = size == 2 && sourceType.SizeOf() == 1 && IsSignedType(sourceType);
            bool needZeroExt = size > sourceType.SizeOf() && !IsSignedType(sourceType) && !needSignExt;

            if (srcReg != reg) Emit("MOV", reg, srcReg);
            else if (!needSignExt && !needZeroExt && srcReg == reg)
            {
                // Source already in target reg; still need to populate high bytes if multi-byte
                if (size >= 2) Emit("MOV", regH, GetHighReg(srcReg));
                if (size == 4) { Emit("MOV", regB2, $"R{int.Parse(srcReg[1..]) + 2}"); Emit("MOV", regB3, $"R{int.Parse(srcReg[1..]) + 3}"); }
                return;
            }

            if (needZeroExt)
            {
                if (size >= 2) Emit("CLR", regH);
                if (size == 4) { Emit("CLR", regB2); Emit("CLR", regB3); }
            }
            else
            {
                if (size >= 2 && !needSignExt) Emit("MOV", regH, GetHighReg(srcReg));
                if (size == 4) { Emit("MOV", regB2, $"R{int.Parse(srcReg[1..]) + 2}"); Emit("MOV", regB3, $"R{int.Parse(srcReg[1..]) + 3}"); }
            }

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
            bool needZeroExt = size > sourceType.SizeOf() && !IsSignedType(sourceType) && !needSignExt;

            if (tmpReg != reg) Emit("MOV", reg, tmpReg);
            if (needZeroExt)
            {
                if (size >= 2) Emit("CLR", regH);
                if (size == 4) { Emit("CLR", regB2); Emit("CLR", regB3); }
            }
            else
            {
                if (size >= 2 && !needSignExt) Emit("MOV", regH, GetHighReg(tmpReg));
                if (size == 4) { Emit("MOV", regB2, $"R{int.Parse(tmpReg[1..]) + 2}"); Emit("MOV", regB3, $"R{int.Parse(tmpReg[1..]) + 3}"); }
            }

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
            bool needZeroExt = size > sourceType.SizeOf() && !IsSignedType(sourceType) && !needSignExt;

            if (nearY)
            {
                Emit("LDD", reg, $"Y+{offset}");
                if (needZeroExt)
                {
                    if (size >= 2) Emit("CLR", regH);
                    if (size == 4) { Emit("CLR", regB2); Emit("CLR", regB3); }
                }
                else
                {
                    if (size >= 2 && !needSignExt) Emit("LDD", regH,  $"Y+{offset + 1}");
                    if (size == 4) { Emit("LDD", regB2, $"Y+{offset + 2}"); Emit("LDD", regB3, $"Y+{offset + 3}"); }
                }
            }
            else
            {
                var abs = 0x0100 + offset;
                Emit("LDS", reg, $"0x{abs:X4}");
                if (needZeroExt)
                {
                    if (size >= 2) Emit("CLR", regH);
                    if (size == 4) { Emit("CLR", regB2); Emit("CLR", regB3); }
                }
                else
                {
                    if (size >= 2 && !needSignExt) Emit("LDS", regH,  $"0x{abs + 1:X4}");
                    if (size == 4) { Emit("LDS", regB2, $"0x{abs + 2:X4}"); Emit("LDS", regB3, $"0x{abs + 3:X4}"); }
                }
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
        bool zeroExt = size > srcType.SizeOf() && !IsSignedType(srcType) && !signExt;

        Emit("LDS", reg, addr);
        if (zeroExt)
        {
            if (size >= 2) Emit("CLR", regH);
            if (size == 4) { Emit("CLR", regB2); Emit("CLR", regB3); }
        }
        else
        {
            if (size >= 2 && !signExt) Emit("LDS", regH, addr + "+1");
            if (size == 4) { Emit("LDS", regB2, addr + "+2"); Emit("LDS", regB3, addr + "+3"); }
        }

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
        _flashArrayPool.Clear();
        _allTmpRegNames.Clear();
        _labelCounter = 0;

        var allocator = new StackAllocator();
        var (offsets, maxStack) = allocator.Allocate(program);
        _stackLayout = offsets;
        _maxStaticUsage = maxStack;
        _needsGc = program.NeedsGc;
        _varSizes = allocator.VariableSizes;
        _regLayout = AvrRegisterAllocator.Allocate(program);

        // Build set of float-typed variable names for correct register assignment.
        _varIsFloat = [];
        foreach (var func in program.Functions)
            foreach (var instr in func.Body)
            {
                var valsToCheck = instr switch
                {
                    Binary b => new[] { b.Src1, b.Src2, b.Dst },
                    Copy c => new[] { c.Src, c.Dst },
                    Return { Value: not null } r => new[] { r.Value! },
                    Call cl => [.. cl.Args, cl.Dst],
                    _ => Array.Empty<Val>()
                };
                foreach (var v in valsToCheck)
                {
                    if (v is Variable vv && vv.Type == DataType.FLOAT) _varIsFloat.Add(vv.Name);
                    if (v is Temporary tt && tt.Type == DataType.FLOAT) _varIsFloat.Add(tt.Name);
                }
            }

        // Build function parameter size map for correct call-site arg loading.
        _functionParamSizes.Clear();
        foreach (var func in program.Functions)
        {
            var sizes = new List<int>();
            foreach (var p in func.Params)
                sizes.Add(_varSizes.TryGetValue(p, out int sz) ? sz : 1);
            _functionParamSizes[func.Name] = sizes;
        }

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

        if (program.NeedsGc)
            EmitGcSramLayout();

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

        EmitFlashArrayPool(output);
        if (program.NeedsGc) EmitGcRuntime(output);
        // Emit the exception runtime when either the SJLJ model (setjmp) is used
        // OR when the T-flag model calls __pymcu_unhandled_exn for unmatched catches.
        bool needsExnRuntime = program.ExternSymbols.Contains("setjmp")
            || program.Functions.Any(f =>
                f.Body.OfType<Call>().Any(c => c.FunctionName == "__pymcu_unhandled_exn"));
        if (needsExnRuntime) EmitExnRuntime(output, _usedExnCodes, cfg.Chip);
        WriteSymbolsIfRequested(optimized, program);
        WriteLineMapIfRequested(optimized);
        WriteVarMapIfRequested(program);
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

        if (func.IsInterrupt && !func.IsNaked) EmitContextSave();

        if (func.Name == "main")
        {
            Emit("LDI", "R16", "hi8(0x08FF)");
            Emit("OUT", "0x3E", "R16");
            Emit("LDI", "R16", "lo8(0x08FF)");
            Emit("OUT", "0x3D", "R16");
            Emit("LDI", "R28", "lo8(_stack_base)");
            Emit("LDI", "R29", "hi8(_stack_base)");
            if (_needsGc) Emit("CALL", "gc_init");
        }

        if (!func.IsInterrupt && func.Name != "main" && func.Params.Count > 0)
        {
            string[] argRegs = ["R24", "R22", "R20", "R18"];
            for (var k = 0; k < func.Params.Count && k < 4; k++)
            {
                var pname = func.Params[k];
                bool p16 = _varSizes.TryGetValue(pname, out int psz) && psz == 2;
                bool p32 = _varSizes.TryGetValue(pname, out int psz32) && psz32 == 4;
                bool pFloat = p32 && _varIsFloat.Contains(pname);
                // Float ABI: first float arg is in R22(byte0):R23(byte1):R24(byte2):R25(byte3).
                // uint32 ABI: first arg is in R24(byte0):R25(byte1):R22(byte2):R23(byte3).
                // For floats, use R22 as base; for uint32, use R24 as base.
                string aR = pFloat && k == 0 ? "R22" : argRegs[k];
                if (_regLayout.TryGetValue(pname, out var r))
                {
                    if (aR != r) Emit("MOV", r, aR);
                    if (p16 || p32) Emit("MOV", GetHighReg(r), GetHighReg(aR));
                    if (p32)
                    {
                        // For float k==0: bytes 2 and 3 are in R24 and R25.
                        // For uint32 k==0: bytes 2 and 3 are in R22 and R23.
                        string aR2 = pFloat && k == 0 ? "R24" : (k == 0 ? "R22" : $"R{int.Parse(aR[1..]) + 2}");
                        string aR3 = pFloat && k == 0 ? "R25" : (k == 0 ? "R23" : $"R{int.Parse(aR[1..]) + 3}");
                        Emit("MOV", $"R{int.Parse(r[1..]) + 2}", aR2);
                        Emit("MOV", $"R{int.Parse(r[1..]) + 3}", aR3);
                    }
                }
                else if (_stackLayout.TryGetValue(pname, out int off))
                {
                    int pSize = p32 ? 4 : (p16 ? 2 : 1);
                    bool nearY = off + (pSize - 1) < 64;
                    if (nearY)
                    {
                        Emit("STD", $"Y+{off}", aR);
                        if (p16 || p32) Emit("STD", $"Y+{off + 1}", GetHighReg(aR));
                        if (p32)
                        {
                            string aR2 = pFloat && k == 0 ? "R24" : (k == 0 ? "R22" : $"R{int.Parse(aR[1..]) + 2}");
                            string aR3 = pFloat && k == 0 ? "R25" : (k == 0 ? "R23" : $"R{int.Parse(aR[1..]) + 3}");
                            Emit("STD", $"Y+{off + 2}", aR2);
                            Emit("STD", $"Y+{off + 3}", aR3);
                        }
                    }
                    else
                    {
                        var abs = 0x0100 + off;
                        Emit("STS", $"0x{abs:X4}", aR);
                        if (p16 || p32) Emit("STS", $"0x{abs + 1:X4}", GetHighReg(aR));
                        if (p32)
                        {
                            string aR2 = pFloat && k == 0 ? "R24" : (k == 0 ? "R22" : $"R{int.Parse(aR[1..]) + 2}");
                            string aR3 = pFloat && k == 0 ? "R25" : (k == 0 ? "R23" : $"R{int.Parse(aR[1..]) + 3}");
                            Emit("STS", $"0x{abs + 2:X4}", aR2);
                            Emit("STS", $"0x{abs + 3:X4}", aR3);
                        }
                    }
                }
            }
        }

        bool emittedEpilogue = false;
        foreach (var instr in func.Body)
        {
            if (func.IsInterrupt && !func.IsNaked && instr is Return)
            {
                EmitContextRestore();
                Emit("RETI");
                emittedEpilogue = true;
                continue;
            }

            CompileInstruction(instr);
        }

        if (func.IsInterrupt && !func.IsNaked && !emittedEpilogue)
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
                    : $"{d.SourceFile}:{d.Line}: {d.Text}");
                if (!string.IsNullOrEmpty(d.SourceFile) && !d.IsInline)
                    _assembly.Add(AvrAsmLine.MakeDebugMarker(d.SourceFile, d.Line));
                break;
            case JumpIfEqual je: CompileCompareJump(je.Src1, je.Src2, "BREQ", je.Target); break;
            case JumpIfNotEqual jne: CompileCompareJump(jne.Src1, jne.Src2, "BRNE", jne.Target); break;
            case JumpIfLessThan jlt: CompileCompareJump(jlt.Src1, jlt.Src2, IsSignedComparison(jlt.Src1, jlt.Src2) ? "BRLT" : "BRLO", jlt.Target); break;
            case JumpIfLessOrEqual jle: CompileLessOrEqual(jle); break;
            case JumpIfGreaterThan jgt: CompileGreaterThan(jgt); break;
            case JumpIfGreaterOrEqual jge: CompileCompareJump(jge.Src1, jge.Src2, IsSignedComparison(jge.Src1, jge.Src2) ? "BRGE" : "BRSH", jge.Target); break;
            case Call c: CompileCall(c); break;
            case IndirectCall ic: CompileIndirectCall(ic); break;
            case Copy cp: CompileCopy(cp); break;
            case Bitcast bc: CompileBitcast(bc); break;
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
            case InlineAsm asm2:
                if (asm2.Operands == null || asm2.Operands.Count == 0)
                {
                    _assembly.Add(AvrAsmLine.MakeRaw(InterpolateAsmSymbols(asm2.Code)));
                }
                else
                {
                    CompileInlineAsmWithConstraints(asm2);
                }
                break;
            case ArrayLoad al: CompileArrayLoad(al); break;
            case ArrayLoadFlash alf: CompileArrayLoadFlash(alf); break;
            case FlashData fd: _flashArrayPool[fd.Name] = fd.Bytes; break;
            case ArrayStore ast: CompileArrayStore(ast); break;
            case BytearrayLoad bl: CompileBytearrayLoad(bl); break;
            case BytearrayStore bs2: CompileBytearrayStore(bs2); break;
            case GcAlloc ga:  CompileGcAlloc(ga);  break;
            case GcRoot gr:   CompileGcRoot(gr);   break;
            case GcUnroot gu: CompileGcUnroot(gu); break;
            case TryBegin tb: CompileTryBegin(tb); break;
            case RaiseExn re: CompileRaiseExn(re); break;
            case SignalError se: CompileSignalError(se); break;
            case SignalSuccess: CompileSignalSuccess(); break;
            case BranchOnError boe: CompileBranchOnError(boe); break;
        }
    }

    private void CompileReturn(Return r)
    {
        if (r.Value is not NoneVal)
        {
            var returnType = _currentFunction?.ReturnType ?? GetValType(r.Value);
            if (returnType == DataType.FLOAT)
                LoadFloatIntoRegs(r.Value);
            else
                LoadIntoReg(r.Value, "R24", returnType);
        }

        // Inject CLT before RET in every CanFail function's happy path.
        // This guarantees T == 0 when the caller reads the flag, regardless of
        // which other CanFail callees the function may have called along the way.
        // ISRs are excluded: (a) they cannot be CanFail (enforced by CanFailAnalyzer)
        // and (b) SREG is restored from the saved context by EmitContextRestore().
        if ((_currentFunction?.CanFail ?? false) && !(_currentFunction?.IsInterrupt ?? false))
            Emit("CLT");

        if (!(_currentFunction?.IsNaked ?? false))
            Emit("RET");
    }

    private void CompileJumpIfZero(JumpIfZero jz)
    {
        var type = GetValType(jz.Condition);
        LoadIntoReg(jz.Condition, "R24", type);

        if (type.SizeOf() == 4)
        {
            Emit("OR", "R24", "R25");
            Emit("OR", "R24", "R22");
            Emit("OR", "R24", "R23");
            EmitBranch("BREQ", jz.Target);
        }
        else if (type.SizeOf() == 2)
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
        if (type.SizeOf() == 4) { Emit("OR", "R24", "R25"); Emit("OR", "R24", "R22"); Emit("OR", "R24", "R23"); }
        else if (type.SizeOf() == 2) Emit("OR", "R24", "R25");
        else Emit("TST", "R24");
        EmitBranch("BRNE", jnz.Target);
    }

    private void EmitCompare(Val src1, Val src2, DataType type)
    {
        LoadIntoReg(src1, "R24", type);
        if (src2 is Constant c)
        {
            var val = c.Value;
            if (type.SizeOf() == 4)
            {
                Emit("LDI", "R18", $"{val & 0xFF}");
                Emit("LDI", "R19", $"{(val >> 8) & 0xFF}");
                Emit("LDI", "R20", $"{(val >> 16) & 0xFF}");
                Emit("LDI", "R21", $"{(val >> 24) & 0xFF}");
                Emit("CP",  "R24", "R18");
                Emit("CPC", "R25", "R19");
                Emit("CPC", "R22", "R20");
                Emit("CPC", "R23", "R21");
            }
            else if (type.SizeOf() == 2)
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
            if (type.SizeOf() == 4) { Emit("CPC", "R25", "R19"); Emit("CPC", "R22", "R20"); Emit("CPC", "R23", "R21"); }
        }
    }

    private void CompileCompareJump(Val src1, Val src2, string branch, string target)
    {
        var type = GetValType(src1);
        if (type == DataType.FLOAT)
        {
            // __fp_cmp(arg0=R22:R25, arg1=R18:R21): returns 0xFF if <, 0x00 if =, 0x01 if >
            LoadFloatIntoRegs(src1);
            Emit("PUSH", "R25");
            Emit("PUSH", "R24");
            Emit("PUSH", "R23");
            Emit("PUSH", "R22");
            LoadFloatIntoRegs(src2);
            Emit("MOV", "R18", "R22");
            Emit("MOV", "R19", "R23");
            Emit("MOV", "R20", "R24");
            Emit("MOV", "R21", "R25");
            Emit("POP", "R22");
            Emit("POP", "R23");
            Emit("POP", "R24");
            Emit("POP", "R25");
            Emit("CALL", "__cmpsf2");
            // Map branch to __cmpsf2 result: 0xFF=lt, 0x00=eq, 0x01=gt
            switch (branch)
            {
                case "BRGE": case "BRSH":
                    Emit("CPI", "R24", "0xFF"); EmitBranch("BRNE", target); break;
                case "BRLT": case "BRLO":
                    Emit("CPI", "R24", "0xFF"); EmitBranch("BREQ", target); break;
                case "BREQ":
                    Emit("CPI", "R24", "0x00"); EmitBranch("BREQ", target); break;
                case "BRNE":
                    Emit("CPI", "R24", "0x00"); EmitBranch("BRNE", target); break;
                default:
                    Emit("CPI", "R24", "0xFF"); EmitBranch("BRNE", target); break;
            }
            return;
        }
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
            if (type.SizeOf() == 4)
            {
                if (val < int.MaxValue)
                {
                    long cmpVal = (long)val + 1;
                    Emit("LDI", "R18", $"{cmpVal & 0xFF}");
                    Emit("LDI", "R19", $"{(cmpVal >> 8) & 0xFF}");
                    Emit("LDI", "R20", $"{(cmpVal >> 16) & 0xFF}");
                    Emit("LDI", "R21", $"{(cmpVal >> 24) & 0xFF}");
                    Emit("CP",  "R24", "R18");
                    Emit("CPC", "R25", "R19");
                    Emit("CPC", "R22", "R20");
                    Emit("CPC", "R23", "R21");
                    EmitBranch(signed ? "BRGE" : "BRSH", jgt.Target);
                }
                return;
            }
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
        if (type.SizeOf() == 4) { Emit("CPC", "R25", "R19"); Emit("CPC", "R22", "R20"); Emit("CPC", "R23", "R21"); }
        var skip = MakeLabel("L_BRHI_SKIP");
        Emit("BREQ", skip);
        EmitBranch(signed ? "BRGE" : "BRSH", jgt.Target);
        EmitLabel(skip);
    }

    private void CompileCall(Call call)
    {
        // Float arguments use R22:R25 (arg0) and R18:R21 (arg1) per float convention.
        // Integer arguments use R24 (arg0), R22 (arg1), R20 (arg2), R18 (arg3).
        string[] argRegs = ["R24", "R22", "R20", "R18"];
        for (var k = 0; k < call.Args.Count && k < 4; k++)
        {
            var argType = GetValType(call.Args[k]);
            if (argType == DataType.FLOAT)
            {
                // Float arg0 → R22:R25; float arg1 → R18:R21
                if (k == 0)
                    LoadFloatIntoRegs(call.Args[k]);
                else if (k == 1)
                {
                    LoadFloatIntoRegs(call.Args[k]);
                    Emit("MOV", "R18", "R22");
                    Emit("MOV", "R19", "R23");
                    Emit("MOV", "R20", "R24");
                    Emit("MOV", "R21", "R25");
                }
                continue;
            }
            // Use the declared parameter size when available so that constants
            // (e.g. Constant(-1) for an int16 param) are loaded with the correct
            // width instead of the size inferred from the constant's magnitude.
            if (_functionParamSizes.TryGetValue(call.FunctionName, out var paramSizes) &&
                k < paramSizes.Count)
            {
                int paramSize = paramSizes[k];
                if (paramSize >= 2 && argType.SizeOf() < paramSize)
                    argType = paramSize == 4 ? DataType.UINT32 : DataType.UINT16;
            }
            LoadIntoReg(call.Args[k], argRegs[k], argType);
        }

        Emit("CALL", call.FunctionName);
        var dstType = GetValType(call.Dst);
        if (dstType == DataType.FLOAT)
            StoreFloatFromRegs(call.Dst);
        else
            StoreRegInto("R24", call.Dst, dstType);
    }

    // Indirect call through a function pointer (ICALL Z on AVR).
    // FuncAddr must be a FUNCREF-typed variable or a FunctionRef Val.
    // The function address is loaded into Z (R30:R31) and ICALL is emitted.
    private void CompileIndirectCall(IndirectCall call)
    {
        // Set up arguments into standard registers (same ABI as direct Call).
        string[] argRegs = ["R24", "R22", "R20", "R18"];
        for (var k = 0; k < call.Args.Count && k < 4; k++)
        {
            var argType = GetValType(call.Args[k]);
            LoadIntoReg(call.Args[k], argRegs[k], argType);
        }

        // Load function address into Z register (R30:R31).
        LoadIntoReg(call.FuncAddr, "R30", DataType.UINT16);

        Emit("ICALL");

        var dstType = GetValType(call.Dst);
        StoreRegInto("R24", call.Dst, dstType);
    }

    private void CompileCopy(Copy cp)
    {
        var srcType = GetValType(cp.Src);
        var dstType = GetValType(cp.Dst);
        if (srcType == DataType.FLOAT && dstType != DataType.FLOAT)
        {
            // Float → integer: load float into R22:R25, __fixsfsi converts to int32 (R22=LSB).
            LoadFloatIntoRegs(cp.Src);
            Emit("CALL", "__fixsfsi");
            Emit("MOV", "R24", "R22");
            Emit("MOV", "R25", "R23");
            StoreRegInto("R24", cp.Dst, dstType);
            return;
        }
        if (srcType == DataType.FLOAT || dstType == DataType.FLOAT)
        {
            LoadFloatIntoRegs(cp.Src);
            StoreFloatFromRegs(cp.Dst);
            return;
        }
        // When src is a typeless constant, use the destination's declared type
        // to ensure e.g. `i: uint16 = 0` initialises both bytes.
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

    private void CompileBitcast(Bitcast bc)
    {
        var srcType = GetValType(bc.Src);
        var dstType = GetValType(bc.Dst);
        bool srcIsFloat = srcType == DataType.FLOAT;
        bool dstIsFloat = dstType == DataType.FLOAT;

        if (srcIsFloat && !dstIsFloat)
        {
            // float → intN: float reg layout: R22=b0, R23=b1, R24=b2, R25=b3
            // uint32 reg layout:              R24=b0, R25=b1, R22=b2, R23=b3
            LoadFloatIntoRegs(bc.Src);
            Emit("PUSH", "R24");        // save b2
            Emit("PUSH", "R25");        // save b3
            Emit("MOV", "R24", "R22"); // R24 = b0
            Emit("MOV", "R25", "R23"); // R25 = b1
            Emit("POP", "R23");        // R23 = b3
            Emit("POP", "R22");        // R22 = b2
            StoreRegInto("R24", bc.Dst, dstType);
        }
        else if (!srcIsFloat && dstIsFloat)
        {
            // intN → float: always treat source as 32-bit (float is 4 bytes).
            // GetValType(Constant) only returns UINT8/UINT16; force UINT32 so all
            // 4 bytes of the constant are loaded into R24:R25:R22:R23.
            // uint32 reg layout: R24=b0, R25=b1, R22=b2, R23=b3
            // float reg layout:  R22=b0, R23=b1, R24=b2, R25=b3
            LoadIntoReg(bc.Src, "R24", DataType.UINT32);
            Emit("PUSH", "R22");        // save b2
            Emit("PUSH", "R23");        // save b3
            Emit("MOV", "R22", "R24"); // R22 = b0
            Emit("MOV", "R23", "R25"); // R23 = b1
            Emit("POP", "R25");        // R25 = b3
            Emit("POP", "R24");        // R24 = b2
            StoreFloatFromRegs(bc.Dst);
        }
        else
        {
            // No float involved: same byte layout, just type reinterpretation
            CompileCopy(new Copy(bc.Src, bc.Dst));
        }
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
        // Delegate float operations to soft-float routines.
        if (type == DataType.FLOAT
            || GetValType(b.Src1) == DataType.FLOAT
            || GetValType(b.Src2) == DataType.FLOAT)
        {
            CompileFloatBinary(b);
            return;
        }
        // For Div/Mod and shift ops the source may be wider than the destination
        // (e.g. uint16 >> 8 → uint8 must operate as 16-bit to read the high byte).
        var src1Type = GetValType(b.Src1);
        var opType = (b.Op is IrBinOp.Div or IrBinOp.FloorDiv or IrBinOp.Mod
                             or IrBinOp.RShift or IrBinOp.LShift or IrBinOp.BitAnd
                             or IrBinOp.BitOr or IrBinOp.BitXor)
                     && src1Type.SizeOf() > type.SizeOf()
            ? src1Type
            : type;
        bool is16 = opType.SizeOf() == 2;
        bool is32 = opType.SizeOf() == 4;
        LoadIntoReg(b.Src1, "R24", opType);

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
                    case IrBinOp.RShift:
                    {
                        int byteShift = val / 8;
                        int bitShift  = val % 8;
                        bool s16 = IsSignedType(opType);
                        if (byteShift >= 2)
                        {
                            if (s16) { Emit("MOV","R24","R25"); Emit("LSL","R24"); Emit("SBC","R24","R24"); Emit("CLR","R25"); }
                            else { Emit("CLR","R24"); Emit("CLR","R25"); }
                        }
                        else if (byteShift == 1) { Emit("MOV","R24","R25"); Emit("CLR","R25"); }
                        for (int i = 0; i < bitShift; i++)
                        {
                            if (s16) Emit("ASR","R25"); else Emit("LSR","R25");
                            Emit("ROR","R24");
                        }
                        usedImm = true;
                        break;
                    }
                    case IrBinOp.LShift:
                    {
                        int byteShift = val / 8;
                        int bitShift  = val % 8;
                        if (byteShift >= 2) { Emit("CLR","R24"); Emit("CLR","R25"); }
                        else if (byteShift == 1) { Emit("MOV","R25","R24"); Emit("CLR","R24"); }
                        for (int i = 0; i < bitShift; i++) { Emit("LSL","R24"); Emit("ROL","R25"); }
                        usedImm = true;
                        break;
                    }
                }
            }
        }

        if (!usedImm) LoadIntoReg(b.Src2, "R18", opType);

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
            if (bw.Src is Constant c)
            {
                if (c.Value != 0)
                    Emit("SBI", $"0x{mem.Address - 0x20:X2}", $"{bw.Bit}");
                else
                    Emit("CBI", $"0x{mem.Address - 0x20:X2}", $"{bw.Bit}");
                return;
            }
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

        if (bw.Src is Constant cv)
        {
            LoadIntoReg(bw.Target, "R18");
            if (cv.Value != 0)
                Emit("ORI", "R18", $"{1 << bw.Bit}");
            else
                Emit("ANDI", "R18", $"{(byte)~(1 << bw.Bit)}");
            StoreRegInto("R18", bw.Target);
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

    // Load one byte from a bytearray pointer parameter: dst = ptr[index].
    private void CompileBytearrayLoad(BytearrayLoad bl)
    {
        // Load the 16-bit pointer into Z.
        // Prefer register-allocated location (higher priority than stack).
        if (_regLayout.TryGetValue(bl.PtrName, out string? baseRegL) && baseRegL != null)
        {
            Emit("MOV", "R30", baseRegL);
            Emit("MOV", "R31", GetHighReg(baseRegL));
        }
        else if (_stackLayout.TryGetValue(bl.PtrName, out int ptrOffset))
        {
            Emit("LDD", "R30", $"Y+{ptrOffset}");
            Emit("LDD", "R31", $"Y+{ptrOffset + 1}");
        }
        else
        {
            EmitComment("BytearrayLoad: pointer location unknown -- skip");
            return;
        }

        // Add index to Z, then load the byte.
        // Use ADIW for constant indices 1-63 to avoid using R16/R17 as scratch
        // (which would clobber register-allocated temporaries).
        if (bl.Index is Constant cIdx && cIdx.Value == 0)
        {
            Emit("LD", "R24", "Z");
        }
        else if (bl.Index is Constant cIdx2 && cIdx2.Value is >= 1 and <= 63)
        {
            Emit("ADIW", "R30", $"{cIdx2.Value}");
            Emit("LD", "R24", "Z");
        }
        else if (bl.Index is Constant cIdx3)
        {
            Emit("LDI", "R16", $"{cIdx3.Value}");
            Emit("CLR", "R17");
            Emit("ADD", "R30", "R16");
            Emit("ADC", "R31", "R17");
            Emit("LD", "R24", "Z");
        }
        else
        {
            LoadIntoReg(bl.Index, "R16");
            Emit("CLR", "R17");
            Emit("ADD", "R30", "R16");
            Emit("ADC", "R31", "R17");
            Emit("LD", "R24", "Z");
        }

        StoreRegInto("R24", bl.Dst, DataType.UINT8);
    }

    // Store one byte to a bytearray pointer parameter: ptr[index] = src.
    private void CompileBytearrayStore(BytearrayStore bs)
    {
        // Load source value into R18 (scratch, safe across Z setup).
        LoadIntoReg(bs.Src, "R18", DataType.UINT8);

        // Load the 16-bit pointer into Z.
        // Prefer register-allocated location (higher priority than stack).
        if (_regLayout.TryGetValue(bs.PtrName, out string? baseRegS) && baseRegS != null)
        {
            Emit("MOV", "R30", baseRegS);
            Emit("MOV", "R31", GetHighReg(baseRegS));
        }
        else if (_stackLayout.TryGetValue(bs.PtrName, out int ptrOffset))
        {
            Emit("LDD", "R30", $"Y+{ptrOffset}");
            Emit("LDD", "R31", $"Y+{ptrOffset + 1}");
        }
        else
        {
            EmitComment("BytearrayStore: pointer location unknown -- skip");
            return;
        }

        // Add index to Z, then store.
        // Use ADIW for constant indices 1-63 to avoid using R16/R17 as scratch
        // (which would clobber register-allocated temporaries).
        if (bs.Index is Constant cIdx && cIdx.Value == 0)
        {
            Emit("ST", "Z", "R18");
        }
        else if (bs.Index is Constant cIdx2 && cIdx2.Value is >= 1 and <= 63)
        {
            Emit("ADIW", "R30", $"{cIdx2.Value}");
            Emit("ST", "Z", "R18");
        }
        else if (bs.Index is Constant cIdx3)
        {
            Emit("LDI", "R16", $"{cIdx3.Value}");
            Emit("CLR", "R17");
            Emit("ADD", "R30", "R16");
            Emit("ADC", "R31", "R17");
            Emit("ST", "Z", "R18");
        }
        else
        {
            LoadIntoReg(bs.Index, "R16");
            Emit("CLR", "R17");
            Emit("ADD", "R30", "R16");
            Emit("ADC", "R31", "R17");
            Emit("ST", "Z", "R18");
        }
    }

    private static readonly Regex _asmInterpPattern =
        new(@"\{([A-Za-z_][A-Za-z0-9_]*)\}", RegexOptions.Compiled);

    // Replace {varname} placeholders in an asm() string with the actual .equ symbol
    // emitted for that Python variable. _stackLayout keys use the mangled form that is
    // already the asm symbol name: module prefix + "_" + varname (e.g. "rtos__cur_task").
    // Function names use the same scheme: "rtos__systick". We find the variable by looking
    // for a key that ends with the identifier and whose prefix is a prefix of the current
    // function name. If not found the placeholder is left unchanged (asm label, etc.).
    private string InterpolateAsmSymbols(string code)
    {
        var funcName = _currentFunction?.Name ?? "";

        return _asmInterpPattern.Replace(code, m =>
        {
            var id = m.Groups[1].Value;

            // 1. Bare lookup — main-module globals have no prefix in key
            if (_stackLayout.ContainsKey(id))
                return id;
            if (_regLayout.ContainsKey(id))
                throw new InvalidOperationException(
                    $"asm() interpolation: '{id}' is register-allocated; use a Python local to copy it first");

            // 2. Prefixed lookup — key = modulePrefix + id, e.g. "rtos_" + "_cur_task"
            //    The module prefix of the current function is the part of funcName before
            //    the function's own name, i.e. the prefix shared with the variable key.
            string? moduleMatch = null;
            string? crossModuleMatch = null;
            bool crossModuleAmbiguous = false;

            foreach (var key in _stackLayout.Keys)
            {
                if (!key.EndsWith(id, StringComparison.Ordinal)) continue;
                if (key.Length == id.Length) continue; // bare — already handled above

                var prefix = key.Substring(0, key.Length - id.Length); // e.g. "rtos_"
                if (funcName.StartsWith(prefix, StringComparison.Ordinal))
                {
                    moduleMatch = key;
                    break; // module match takes priority
                }
                if (crossModuleMatch == null)
                    crossModuleMatch = key;
                else
                    crossModuleAmbiguous = true;
            }

            if (moduleMatch != null)
                return moduleMatch;

            // Also check regLayout for same-module variables (give clear error)
            foreach (var key in _regLayout.Keys)
            {
                if (!key.EndsWith(id, StringComparison.Ordinal)) continue;
                if (key.Length == id.Length) continue;
                var prefix = key.Substring(0, key.Length - id.Length);
                if (funcName.StartsWith(prefix, StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        $"asm() interpolation: '{id}' is register-allocated; use a Python local to copy it first");
            }

            // 3. Unambiguous cross-module match
            if (crossModuleMatch != null && !crossModuleAmbiguous)
                return crossModuleMatch;

            // 4. Not found — leave unchanged (ASM label, forward reference, etc.)
            return m.Value;
        });
    }

    private void CompileInlineAsmWithConstraints(InlineAsm ia)
    {
        // %N constraint substitution: load operand N into scratch register R1{6+N},
        // substitute %N in the template, emit the assembly, then store back.
        // Scratch registers: %0→R16, %1→R17, %2→R18, %3→R19 (uint8 only).
        if (ia.Operands == null || ia.Operands.Count == 0) return;
        if (ia.Operands.Count > 4)
            throw new InvalidOperationException("asm() constraint: maximum 4 operands (%0–%3)");

        var scratchRegs = new[] { "R16", "R17", "R18", "R19" };

        // Load each operand into its scratch register.
        for (int i = 0; i < ia.Operands.Count; i++)
            LoadIntoReg(ia.Operands[i], scratchRegs[i], DataType.UINT8);

        // Substitute %N → RNN in the template and emit.
        var code = ia.Code;
        for (int i = ia.Operands.Count - 1; i >= 0; i--)
            code = code.Replace($"%{i}", scratchRegs[i]);
        _assembly.Add(AvrAsmLine.MakeRaw(code));

        // Store result register back into any non-constant operand.
        for (int i = 0; i < ia.Operands.Count; i++)
        {
            if (ia.Operands[i] is not Constant)
                StoreRegInto(scratchRegs[i], ia.Operands[i], DataType.UINT8);
        }
    }

    private void CompileArrayLoadFlash(ArrayLoadFlash alf)
    {
        // Load one byte from a flash-resident const[uint8[N]] table via LPM Z.
        // Table label in flash byte-address space (same as string pool labels).
        var label = "__flash_" + alf.ArrayName.Replace('.', '_');
        LoadIntoReg(alf.Index, "R24");            // index -> R24
        Emit("LDI", "R30", $"lo8({label})");      // ZL = base byte address
        Emit("LDI", "R31", $"hi8({label})");      // ZH = base byte address
        Emit("ADD", "R30", "R24");                // Z += index (8-bit index, no overflow for small tables)
        Emit("ADC", "R31", "R1");                 // propagate carry (R1 = 0 after MUL clears)
        Emit("LPM", "R24", "Z");                  // load byte from flash
        StoreRegInto("R24", alf.Dst, DataType.UINT8);
    }

    private void EmitFlashArrayPool(TextWriter os)
    {
        if (_flashArrayPool.Count == 0) return;
        os.WriteLine();
        os.WriteLine("; --- Flash Array Pool (LPM lookup tables, const[uint8[N]]) ---");
        foreach (var (name, bytes) in _flashArrayPool)
        {
            var label = "__flash_" + name.Replace('.', '_');
            os.WriteLine($"{label}:");
            os.WriteLine("\t.byte " + string.Join(", ", bytes));
            os.WriteLine("\t.balign 2");
        }
    }

    // -------------------------------------------------------------------------
    // GC SRAM Layout
    // Emits .equ directives for shadow stack, heap-top pointer, and heap bounds.
    // Called from Compile() when program.NeedsGc is true, after all static
    // variable .equ directives have been emitted.
    //
    // Layout immediately after static variables (at _stack_base + _maxStaticUsage):
    //   _gc_ss_base       : 128 bytes  (64 shadow-stack slots x 2 bytes each)
    //   _gc_ss_top_addr   : 1 byte     (current shadow-stack depth)
    //   _gc_heap_top_lo   : 1 byte     (lo byte of GC heap write pointer)
    //   _gc_heap_top_hi   : 1 byte     (hi byte of GC heap write pointer)
    //   _heap_start       : start of GC-managed heap
    //   _heap_end         : 0x0880 (leaves ~127 bytes for hardware SP call stack)
    // -------------------------------------------------------------------------
    private void EmitGcSramLayout()
    {
        int ssBase = _maxStaticUsage;   // offset from _stack_base
        int ssTopAddr = ssBase + 128;   // 1 byte after 64-entry shadow stack
        int heapTopLo = ssTopAddr + 1;
        int heapTopHi = heapTopLo + 1;
        int heapStart = heapTopHi + 1;

        EmitRaw($"; --- GC Runtime SRAM Layout ---");
        EmitRaw($".equ _gc_ss_base,     _stack_base + {ssBase}");
        EmitRaw($".equ _gc_ss_top_addr, _stack_base + {ssTopAddr}");
        EmitRaw($".equ _gc_heap_top_lo, _stack_base + {heapTopLo}");
        EmitRaw($".equ _gc_heap_top_hi, _stack_base + {heapTopHi}");
        EmitRaw($".equ _heap_start,     _stack_base + {heapStart}");
        EmitRaw($".equ _heap_end,       0x0880");
        EmitRaw($"; Shadow stack: {ssBase}..{ssBase + 127} ({128} bytes)");
        EmitRaw($"; GC heap: {heapStart}..0x07FF (~{0x0880 - (0x0100 + heapStart)} bytes available)");
        EmitRaw("");
    }

    // -------------------------------------------------------------------------
    // GC Instruction Handlers
    // -------------------------------------------------------------------------

    // GcAlloc: call gc_alloc(size); store result GC_REF in Dst.
    // size is passed in R24 (lo) : R25 (hi) per AVR calling convention.
    // Result user_ptr is returned in R24:R25.
    private void CompileGcAlloc(GcAlloc ga)
    {
        EmitComment("gc_alloc");
        LoadIntoReg(ga.Size, "R24", DataType.UINT16);   // load size lo into R24 (hi stays R25)
        CLR_R25IfNeeded(ga.Size);                        // R25 = 0 for 1-byte sizes
        Emit("CALL", "gc_alloc");
        StoreRegInto("R24", ga.Dst, DataType.GC_REF);   // store returned user_ptr (R24:R25)
    }

    // Ensure R25 = 0 when the size operand is an 8-bit constant or UINT8 variable.
    private void CLR_R25IfNeeded(Val sizeVal)
    {
        bool is8bit = sizeVal switch
        {
            Constant c   => c.Value >= 0 && c.Value <= 255,
            Variable v   => v.Type is DataType.UINT8 or DataType.INT8,
            Temporary t  => t.Type is DataType.UINT8 or DataType.INT8,
            _            => false
        };
        if (is8bit) Emit("CLR", "R25");
    }

    private static void EmitExnRuntime(TextWriter os, HashSet<int> usedCodes, string chip)
    {
        os.WriteLine("; ── Exception runtime ──────────────────────────────────────────────────────");
        var codes = usedCodes.OrderBy(x => x).ToList();
        bool hasUart = chip is "atmega328p" or "atmega328" or "atmega168p" or "atmega168"
                              or "atmega88p" or "atmega88" or "atmega48p" or "atmega48"
                              or "atmega2560" or "atmega32u4";
        if (codes.Count == 0 || !hasUart)
        {
            os.WriteLine("__pymcu_unhandled_exn:");
            os.WriteLine("    cli");
            os.WriteLine("    rjmp .-2");
            os.WriteLine();
            return;
        }
        foreach (int code in codes)
        {
            os.WriteLine($"__exn_str_{code}:");
            os.WriteLine($"    .byte {ExnAsciiBytes(code)}");
        }
        os.WriteLine("    .balign 2");
        os.WriteLine();
        os.WriteLine("__pymcu_unhandled_exn:");
        os.WriteLine("    lds   R16, 0xC1");
        os.WriteLine("    sbrs  R16, 3");
        os.WriteLine("    rjmp  __exn_halt");
        if (codes.Count == 1)
        {
            int code = codes[0];
            os.WriteLine($"    ldi   R30, lo8(__exn_str_{code})");
            os.WriteLine($"    ldi   R31, hi8(__exn_str_{code})");
        }
        else
        {
            foreach (int code in codes)
            {
                os.WriteLine($"    cpi   R22, {code}");
                os.WriteLine($"    breq  __exn_load_{code}");
            }
            os.WriteLine("    rjmp  __exn_halt");
            for (int i = 0; i < codes.Count; i++)
            {
                int code = codes[i];
                os.WriteLine($"__exn_load_{code}:");
                os.WriteLine($"    ldi   R30, lo8(__exn_str_{code})");
                os.WriteLine($"    ldi   R31, hi8(__exn_str_{code})");
                if (i < codes.Count - 1)
                    os.WriteLine("    rjmp  __exn_print_loop");
            }
        }
        os.WriteLine("__exn_print_loop:");
        os.WriteLine("    lpm   R16, Z+");
        os.WriteLine("    tst   R16");
        os.WriteLine("    breq  __exn_halt");
        os.WriteLine("__exn_wait_udre:");
        os.WriteLine("    lds   R17, 0xC0");
        os.WriteLine("    sbrs  R17, 5");
        os.WriteLine("    rjmp  __exn_wait_udre");
        os.WriteLine("    sts   0xC6, R16");
        os.WriteLine("    rjmp  __exn_print_loop");
        os.WriteLine("__exn_halt:");
        os.WriteLine("    cli");
        os.WriteLine("    rjmp  .-2");
        os.WriteLine();
    }

    private static string ExnCodeName(int code) => code switch
    {
        1 => "ValueError",
        2 => "TypeError",
        3 => "IndexError",
        4 => "KeyError",
        5 => "NotImplementedError",
        _ => $"Exception{code}"
    };

    private static string ExnAsciiBytes(int code)
    {
        string name = ExnCodeName(code);
        var bytes = new List<int> { 'E', ':' };
        foreach (char ch in name) bytes.Add(ch);
        bytes.Add(13);
        bytes.Add(10);
        bytes.Add(0);
        return string.Join(", ", bytes);
    }

    // Emit the gc_runtime.S content (embedded resource) into the output .asm file.
    private static void EmitGcRuntime(TextWriter os)
    {
        os.WriteLine();
        os.WriteLine("; --- PyMCU GC Runtime (gc_runtime.S) ---");
        var asm = System.Reflection.Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream("gc_runtime.S")
            ?? throw new Exception("gc_runtime.S embedded resource not found in assembly");
        using var reader = new System.IO.StreamReader(stream);
        os.Write(reader.ReadToEnd());
    }

    // GcRoot: push the SRAM address of the GC_REF variable onto the shadow stack.
    // After the push, the shadow stack depth (_gc_ss_top_addr) is incremented.
    // The shadow-stack entry holds the absolute SRAM address of the local variable
    // so the GC can load and update the GC_REF value stored there.
    private void CompileGcRoot(GcRoot gr)
    {
        string varName = gr.Var switch
        {
            Variable v  => v.Name,
            Temporary t => t.Name,
            _           => throw new Exception("GcRoot: expected Variable or Temporary")
        };

        int sramAddr = GetGcRefSramAddr(varName);
        EmitComment($"gc_root push: {varName} @ 0x{sramAddr:X4}");

        // X = _gc_ss_base; index = _gc_ss_top_addr (1 byte); X += index*2
        // Then store sramAddr as 2 bytes at X; increment _gc_ss_top_addr.
        Emit("LDS",  "R16", "_gc_ss_top_addr");    // R16 = current depth
        Emit("MOV",  "R17", "R16");
        Emit("LSL",  "R17");                        // R17 = depth * 2
        Emit("LDI",  "R26", "lo8(_gc_ss_base)");
        Emit("LDI",  "R27", "hi8(_gc_ss_base)");
        Emit("CLR",  "R18");
        Emit("ADD",  "R26", "R17");
        Emit("ADC",  "R27", "R18");                 // X = _gc_ss_base + depth*2

        Emit("LDI",  "R17", $"lo8(0x{sramAddr:X4})");
        Emit("LDI",  "R18", $"hi8(0x{sramAddr:X4})");
        Emit("ST",   "X+",  "R17");                 // store sramAddr lo
        Emit("ST",   "X",   "R18");                 // store sramAddr hi

        Emit("INC",  "R16");
        Emit("STS",  "_gc_ss_top_addr", "R16");     // depth++
    }

    // GcUnroot: pop one entry from the shadow stack (decrement depth counter).
    private void CompileGcUnroot(GcUnroot gu)
    {
        EmitComment($"gc_unroot pop: {(gu.Var is Variable v ? v.Name : ((Temporary)gu.Var).Name)}");
        Emit("LDS", "R16", "_gc_ss_top_addr");
        Emit("DEC", "R16");
        Emit("STS", "_gc_ss_top_addr", "R16");
    }

    // Resolve the absolute SRAM address of a GC_REF local variable.
    private int GetGcRefSramAddr(string varName)
    {
        if (_stackLayout.TryGetValue(varName, out int offset))
            return 0x0100 + offset;
        throw new Exception($"GcRoot: variable '{varName}' not found in stack layout");
    }

    // ── Symbol map (--emit-symbols) ───────────────────────────────────────────

    /// <summary>
    /// When set, a symbols JSON file is written to this path after compilation.
    /// Format: [{"Name":"main","WordAddr":4}, ...]
    /// </summary>
    public string? EmitSymbolsPath { get; set; }

    private void WriteSymbolsIfRequested(List<AvrAsmLine> optimized, ProgramIR program)
    {
        if (string.IsNullOrEmpty(EmitSymbolsPath)) return;
        var symbols = ComputeSymbolMap(optimized, program);
        File.WriteAllText(EmitSymbolsPath,
            JsonSerializer.Serialize(symbols, AvrSymbolsJsonContext.Default.ListSymbolEntry));
    }

    private static List<SymbolEntry> ComputeSymbolMap(List<AvrAsmLine> lines, ProgramIR program)
    {
        var nonInline = program.Functions.Where(f => !f.IsInline).ToList();
        var funcNames = nonInline.Select(f => f.Name).ToHashSet();
        var funcByName = nonInline.ToDictionary(f => f.Name);

        uint word = 0;
        var result = new List<SymbolEntry>();

        foreach (var line in lines)
        {
            switch (line.Type)
            {
                case AvrAsmLine.LineType.Raw:
                    if (line.Content.StartsWith(".org ", StringComparison.Ordinal))
                    {
                        var raw = line.Content[5..].Trim();
                        word = raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                            ? Convert.ToUInt32(raw[2..], 16)
                            : uint.Parse(raw);
                    }
                    else
                    {
                        // asm() blocks are emitted as Raw lines but still consume flash words.
                        // Count any Raw line that looks like an instruction (not a directive or comment).
                        word += RawLineInstructionWords(line.Content);
                    }
                    break;
                case AvrAsmLine.LineType.Label:
                    if (funcNames.Contains(line.LabelText))
                    {
                        var func = funcByName[line.LabelText];
                        var displayName = !string.IsNullOrEmpty(func.OriginalName) ? func.OriginalName : line.LabelText;
                        result.Add(new SymbolEntry(displayName, word));
                    }
                    break;
                case AvrAsmLine.LineType.Instruction:
                    word += line.Mnemonic is "CALL" or "JMP" or "LDS" or "STS" ? 2u : 1u;
                    break;
            }
        }
        return result;
    }

    // 2-word AVR instructions (by mnemonic prefix, case-insensitive).
    private static readonly HashSet<string> TwoWordMnemonics =
        new(StringComparer.OrdinalIgnoreCase) { "CALL", "JMP", "LDS", "STS" };

    private static uint RawLineInstructionWords(string content)
    {
        // Strip leading whitespace and inline comments.
        var trimmed = content.TrimStart();
        if (trimmed.Length == 0) return 0;

        // Directives start with '.' or ';' (comment), sub-labels end with ':'.
        var firstChar = trimmed[0];
        if (firstChar == '.' || firstChar == ';') return 0;

        // A local label like "_dly_o16mhz:" — no instruction words.
        var spaceIdx = trimmed.IndexOf(' ');
        var mnemonic = spaceIdx >= 0 ? trimmed[..spaceIdx] : trimmed;
        if (mnemonic.EndsWith(':')) return 0;

        // Strip trailing comment from mnemonic if needed.
        var semicolonIdx = mnemonic.IndexOf(';');
        if (semicolonIdx >= 0) mnemonic = mnemonic[..semicolonIdx].TrimEnd();

        return TwoWordMnemonics.Contains(mnemonic) ? 2u : 1u;
    }

    // ── Line map (--emit-linemap) ─────────────────────────────────────────────

    /// <summary>
    /// When set, a linemap JSON file is written to this path after compilation.
    /// Format: [{"File":"src/main.py","Line":42,"WordAddr":128}, ...]
    /// Used by the debug server to map breakpoints to flash addresses.
    /// </summary>
    public string? EmitLineMapPath { get; set; }

    /// <summary>
    /// When set, a varmap JSON file is written to this path after compilation.
    /// Format: [{"Function":"fibonacci","File":"main.py","StartLine":22,"Vars":{"a":"R4","b":"R5",...}}, ...]
    /// Used by the debugger plugin to display named variables instead of raw registers.
    /// </summary>
    public string? EmitVarMapPath { get; set; }

    private void WriteLineMapIfRequested(List<AvrAsmLine> optimized)
    {
        if (string.IsNullOrEmpty(EmitLineMapPath)) return;
        var entries = ComputeLineMap(optimized);
        File.WriteAllText(EmitLineMapPath,
            JsonSerializer.Serialize(entries, AvrLineMapJsonContext.Default.ListLineMapEntry));
    }

    private static List<LineMapEntry> ComputeLineMap(List<AvrAsmLine> lines)
    {
        uint word = 0;
        var seen   = new HashSet<(string, int)>();
        var result = new List<LineMapEntry>();

        // Pending debug marker: set when a DebugMarker line is encountered, overridden by
        // subsequent markers before any instruction, flushed when the first real instruction
        // at the current word is reached. This ensures loop headers that generate only a
        // label (e.g. "while True:") never create a spurious entry that collides with the
        // first body instruction; only the innermost/last marker wins.
        (string file, int line)? pending = null;

        void Flush()
        {
            if (pending is not (var f, var l)) return;
            if (seen.Add((f, l))) result.Add(new LineMapEntry(f, l, word));
            pending = null;
        }

        foreach (var asmLine in lines)
        {
            switch (asmLine.Type)
            {
                case AvrAsmLine.LineType.Raw:
                    if (asmLine.Content.StartsWith(".org ", StringComparison.Ordinal))
                    {
                        var raw = asmLine.Content[5..].Trim();
                        word = raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                            ? Convert.ToUInt32(raw[2..], 16)
                            : uint.Parse(raw);
                    }
                    else
                    {
                        var w = CountRawWords(asmLine.Content);
                        if (w > 0) { Flush(); word += w; }
                    }
                    break;

                case AvrAsmLine.LineType.DebugMarker:
                    // Later markers override earlier ones at the same word position.
                    pending = (asmLine.DebugFile, asmLine.DebugLine);
                    break;

                case AvrAsmLine.LineType.Instruction:
                    Flush();
                    word += asmLine.Mnemonic is "CALL" or "JMP" or "LDS" or "STS" ? 2u : 1u;
                    break;
            }
        }
        return result;
    }

    // Counts AVR word(s) consumed by a single Raw assembly line.
    // Used by ComputeLineMap to account for inline asm bodies (asm() calls)
    // that are emitted as Raw lines instead of Instruction lines.
    private static uint CountRawWords(string content)
    {
        var trimmed = content.Trim();
        if (trimmed.Length == 0 || trimmed[0] == '.' || trimmed[0] == ';')
            return 0;
        // Strip optional "label:" prefix to isolate the instruction part.
        var colonIdx = trimmed.IndexOf(':');
        var instruction = colonIdx >= 0 ? trimmed[(colonIdx + 1)..].TrimStart() : trimmed;
        if (instruction.Length == 0) return 0;
        var spaceIdx = instruction.IndexOfAny(new[] { ' ', '\t' });
        var mnemonic = spaceIdx < 0 ? instruction : instruction[..spaceIdx];
        if (mnemonic.Length == 0 || !char.IsLetter(mnemonic[0])) return 0;
        return string.Equals(mnemonic, "CALL", StringComparison.OrdinalIgnoreCase)
            || string.Equals(mnemonic, "JMP",  StringComparison.OrdinalIgnoreCase)
            || string.Equals(mnemonic, "LDS",  StringComparison.OrdinalIgnoreCase)
            || string.Equals(mnemonic, "STS",  StringComparison.OrdinalIgnoreCase)
            ? 2u : 1u;
    }

    // SRAM base address on AVR (ATmega328P and compatible): data space starts at 0x0100.
    private const int SramBase = 0x0100;

    private void WriteVarMapIfRequested(ProgramIR program)
    {
        if (string.IsNullOrEmpty(EmitVarMapPath)) return;
        var entries = new List<VarMapEntry>();

        foreach (var func in program.Functions.Where(f => !f.IsInline))
        {
            var firstDebug = func.Body
                .OfType<DebugLine>()
                .FirstOrDefault(d => !d.IsInline && !string.IsNullOrEmpty(d.SourceFile));
            if (firstDebug is null) continue;

            var prefix       = func.Name + ".";
            var vars         = new Dictionary<string, string>(StringComparer.Ordinal);
            var varLines     = new Dictionary<string, int>(StringComparer.Ordinal);
            var stackVars    = new Dictionary<string, int>(StringComparer.Ordinal);
            var stackVarLines = new Dictionary<string, int>(StringComparer.Ordinal);
            int curLine      = firstDebug.Line;

            // --- Parameters: live from function entry. Use startLine as their VarLines
            //     entry so the debugger shows them immediately when entering the function.
            foreach (var pname in func.Params)
            {
                if (!pname.StartsWith(prefix, StringComparison.Ordinal)) continue;
                if (_regLayout.TryGetValue(pname, out var pReg))
                {
                    vars.TryAdd(pname, pReg);
                    varLines.TryAdd(pname, firstDebug.Line);
                }
                else if (_stackLayout.TryGetValue(pname, out int pOff))
                {
                    stackVars.TryAdd(pname, SramBase + pOff);
                    stackVarLines.TryAdd(pname, firstDebug.Line);
                }
            }

            // --- Locals: discovered when they first appear as an instruction destination.
            foreach (var instr in func.Body)
            {
                if (instr is DebugLine dl && !dl.IsInline && dl.Line > 0)
                    curLine = dl.Line;

                IEnumerable<Val> dsts = instr switch
                {
                    Binary b         => [b.Dst],
                    Copy c           => [c.Dst],
                    Unary u          => [u.Dst],
                    Call cl          => [cl.Dst],
                    LoadIndirect li  => [li.Dst],
                    BitCheck bc      => [bc.Dst],
                    ArrayLoad al     => [al.Dst],
                    BytearrayLoad bl => [bl.Dst],
                    _                => []
                };
                foreach (var v in dsts)
                {
                    if (v is not Variable vv) continue;
                    // Only include variables that belong to this function (not call-argument
                    // staging writes like "count_bits.v = main.n" appearing in main's body).
                    if (!vv.Name.StartsWith(prefix, StringComparison.Ordinal)) continue;

                    if (_regLayout.TryGetValue(vv.Name, out var reg))
                    {
                        vars.TryAdd(vv.Name, reg);
                        varLines.TryAdd(vv.Name, curLine);
                    }
                    else if (_stackLayout.TryGetValue(vv.Name, out int off))
                    {
                        stackVars.TryAdd(vv.Name, SramBase + off);
                        stackVarLines.TryAdd(vv.Name, curLine);
                    }
                }
            }

            // Emit an entry for every function that has a source location, even when all
            // variables are stack-spilled. Without this, functions like fibonacci (whose
            // locals all fall below the register-allocation threshold) are invisible to the
            // debugger entirely, which prevents the scope from even appearing.
            var paramNames = func.Params
                .Where(p => p.StartsWith(prefix, StringComparison.Ordinal))
                .ToList();
            entries.Add(new VarMapEntry(
                func.Name, firstDebug.SourceFile, firstDebug.Line,
                vars, varLines,
                stackVars.Count > 0 ? stackVars : null,
                stackVarLines.Count > 0 ? stackVarLines : null,
                paramNames.Count > 0 ? paramNames : null));
        }

        File.WriteAllText(EmitVarMapPath,
            JsonSerializer.Serialize(entries, AvrVarMapJsonContext.Default.ListVarMapEntry));
    }

    private void CompileTryBegin(TryBegin tb)
    {
        string jmpBufName = (tb.JmpBufVar as Variable)?.Name ?? "";
        if (!_stackLayout.TryGetValue(jmpBufName, out int jmpBufOffset))
            throw new Exception($"jmpbuf variable '{jmpBufName}' not found in stack layout");

        int jmpBufAddr = 0x0100 + jmpBufOffset;
        Emit("LDI", "R24", $"lo8({jmpBufAddr})");
        Emit("LDI", "R25", $"hi8({jmpBufAddr})");

        if (_stackLayout.TryGetValue("__pymcu_active_jmpbuf", out int activeOffset))
        {
            int activeAddr = 0x0100 + activeOffset;
            Emit("STS", activeAddr.ToString(), "R24");
            Emit("STS", (activeAddr + 1).ToString(), "R25");
        }

        Emit("CALL", "setjmp");

        string exnCodeName = (tb.ExnCodeVar as Variable)?.Name ?? "";
        if (_stackLayout.TryGetValue(exnCodeName, out int exnOffset))
        {
            if (exnOffset < 64)
                Emit("STD", $"Y+{exnOffset}", "R24");
            else
            {
                int exnAddr = 0x0100 + exnOffset;
                Emit("STS", exnAddr.ToString(), "R24");
            }
        }

        Emit("TST", "R24");
        EmitBranch("BRNE", tb.CatchLabel);
    }

    private void CompileRaiseExn(RaiseExn re)
    {
        if (re.Code is Constant c) _usedExnCodes.Add(c.Value);
        LoadIntoReg(re.Code, "R22", DataType.UINT8);
        Emit("CLR", "R23");

        if (_stackLayout.TryGetValue("__pymcu_active_jmpbuf", out int activeOffset))
        {
            int activeAddr = 0x0100 + activeOffset;
            Emit("LDS", "R24", activeAddr.ToString());
            Emit("LDS", "R25", (activeAddr + 1).ToString());
        }
        else
        {
            Emit("LDI", "R24", "0");
            Emit("LDI", "R25", "0");
        }

        string noHandlerLabel = $"L_no_handler_{_labelCounter++}";
        Emit("MOV", "R16", "R24");
        Emit("OR", "R16", "R25");
        Emit("TST", "R16");
        Emit("BREQ", noHandlerLabel);
        Emit("CALL", "longjmp");
        EmitLabel(noHandlerLabel);
        Emit("CALL", "__pymcu_unhandled_exn");
    }

    // -------------------------------------------------------------------------
    // T-flag error propagation (ABI interna PyMCU — reemplaza SJLJ)
    // -------------------------------------------------------------------------

    // SignalError — el callee está propagando un error al caller.
    // Emite SET: pone el T flag del SREG a 1.
    // El error code se guarda en R22 para que el dispatch en el catch site pueda
    // discriminar el tipo; R22 no es el error "carrier" (ese es T), sino el payload.
    private void CompileSignalError(SignalError se)
    {
        // Load the error code into R22 so the catch dispatcher can identify the type.
        if (se.Code is not Constant { Value: 0 })
            LoadIntoReg(se.Code, "R22", DataType.UINT8);

        Emit("SET");   // BSET 6 — T = 1 (signal error to caller)

        // SignalError is terminal: return immediately with T = 1.
        // CompileReturn injects CLT before RET for CanFail success paths — we must
        // bypass that by emitting RET directly here (without CLT) so T stays set.
        Emit("RET");
    }

    // SignalSuccess — el callee retorna en el happy path.
    // Emite CLT: pone el T flag a 0.
    // Se inyecta ANTES de cada RET en funciones CanFail (ver CompileReturn).
    private void CompileSignalSuccess()
    {
        Emit("CLT");   // BCLR 6 — T = 0
    }

    // BranchOnError — tras llamar a una función CanFail, ramifica si T == 1.
    // BRTS tiene un rango de ±63 bytes de PC; para targets lejanos usamos la
    // inversión BRTC/RJMP (mismo patrón que EmitBranch para los otros flags).
    private void CompileBranchOnError(BranchOnError boe)
    {
        // BRTS target  →  si T está set, salta a target.
        // EmitBranch("BRTS", target) emitiría BRTC skip; RJMP target; label skip:
        // lo que es correcto para targets fuera del rango del branch corto.
        EmitBranch("BRTS", boe.ErrorLabel);
    }
}

public record LineMapEntry(string File, int Line, uint WordAddr);

[JsonSerializable(typeof(List<LineMapEntry>))]
internal partial class AvrLineMapJsonContext : JsonSerializerContext { }

// ── Var map (--emit-varmap) ───────────────────────────────────────────────────────────────────

// StackVars/StackVarLines hold variables that are stack-spilled (not in registers).
// The int value is the absolute AVR data-space address (0x0100 + stack offset).
// Params lists parameter names so the debugger can distinguish them from first-line locals.
public record VarMapEntry(
    string Function,
    string File,
    int StartLine,
    Dictionary<string, string> Vars,
    Dictionary<string, int> VarLines,
    Dictionary<string, int>? StackVars = null,
    Dictionary<string, int>? StackVarLines = null,
    List<string>? Params = null);

[JsonSerializable(typeof(List<VarMapEntry>))]
internal partial class AvrVarMapJsonContext : JsonSerializerContext { }