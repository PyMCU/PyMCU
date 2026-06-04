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
    private readonly HashSet<int> _usedExnCodes = [];
    private readonly Dictionary<string, List<int>> _flashArrayPool = new();
    // Maps function name → list of parameter sizes (in bytes) for correct call-site arg loading.
    private Dictionary<string, List<int>> _functionParamSizes = new();
    private int _labelCounter;
    private Function? _currentFunction;
    private int _bssSize;

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
        FloatConstant => DataType.FLOAT,
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
        // ArrayBase: load the 16-bit base address of an array into reg:regH.
        if (val is ArrayBase ab)
        {
            string regH2 = GetHighReg(reg);
            if (_stackLayout.TryGetValue(ab.ArrayName, out int abOffset))
            {
                // Stack array: absolute SRAM address = 0x0100 + stack offset.
                int absAddr = 0x0100 + abOffset;
                Emit("LDI", reg,   $"lo8(0x{absAddr:X4})");
                Emit("LDI", regH2, $"hi8(0x{absAddr:X4})");
            }
            else
            {
                // Module-level (SRAM) array: use the assembly label.
                string label = ab.ArrayName.Replace('.', '_');
                Emit("LDI", reg,   $"lo8({label})");
                Emit("LDI", regH2, $"hi8({label})");
            }
            return;
        }

        int size = type.SizeOf();
        var regH  = size >= 2 ? GetHighReg(reg) : "";
        // For 32-bit: byte2=R22, byte3=R23 (AVR-GCC uint32 convention when base=R24)
        // When base is not R24, fall back to reg+2/+3 (not used for 32-bit in practice)
        var regB2 = size == 4 ? (reg == "R24" ? "R22" : $"R{int.Parse(reg[1..]) + 2}") : "";
        var regB3 = size == 4 ? (reg == "R24" ? "R23" : $"R{int.Parse(reg[1..]) + 3}") : "";

        switch (val)
        {
            case FloatConstant fc:
            {
                // IEEE 754 single bits, stored in R24=B0(LSB), R25=B1, R22=B2, R23=B3(MSB)
                uint bits = BitConverter.SingleToUInt32Bits((float)fc.Value);
                Emit("LDI", reg,   $"0x{bits & 0xFF:X2}");
                Emit("LDI", regH,  $"0x{(bits >> 8) & 0xFF:X2}");
                Emit("LDI", regB2, $"0x{(bits >> 16) & 0xFF:X2}");
                Emit("LDI", regB3, $"0x{(bits >> 24) & 0xFF:X2}");
                return;
            }
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
        _flashArrayPool.Clear();
        _allTmpRegNames.Clear();
        _labelCounter = 0;

        var allocator = new StackAllocator();
        var (offsets, _) = allocator.Allocate(program);
        _stackLayout = offsets;
        _varSizes = allocator.VariableSizes;
        _bssSize = program.Globals.Sum(g => g.Type.SizeOf()) + program.GlobalArrays.Values.Sum();
        _regLayout = AvrRegisterAllocator.Allocate(program);

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

        if (_bssSize > 0)
            EmitRaw($".equ _bss_end, _stack_base + {_bssSize}");

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

        // Always emit the vector table. Unused vectors jump to __bad_interrupt which
        // performs a soft reset, matching avr-libc safety semantics.
        for (var vec = 1; vec <= 25; vec++)
        {
            EmitRaw($".org 0x{vec * 2:X4}");

            if (isrMap.TryGetValue(vec * 2, out var isrFunc))
            {
                Emit("RJMP", isrFunc.Name);
            }
            else
            {
                Emit("RJMP", "__bad_interrupt");
            }
        }

        EmitRaw("");
        EmitLabel("__bad_interrupt");
        Emit("RJMP", "main");
        EmitRaw("");

        foreach (var func in program.Functions.Where(func => func.IsInterrupt))
            CompileFunction(func);
        // --- Call Graph Analysis for DCE ---
        var referencedFuncs = new HashSet<string>();
        var worklist = new Queue<string>();
        
        void AddRef(string name)
        {
            if (referencedFuncs.Add(name))
                worklist.Enqueue(name);
        }

        AddRef("main");
        foreach (var f in program.Functions.Where(f => f.IsInterrupt))
            AddRef(f.Name);
        foreach (var sym in program.ExternSymbols)
            AddRef(sym);

        while (worklist.Count > 0)
        {
            var fName = worklist.Dequeue();
            var f = program.Functions.FirstOrDefault(x => x.Name == fName);
            if (f == null) continue;
            foreach (var instr in f.Body)
            {
                if (instr is Call c)
                {
                    if ((c.FunctionName == "_delay_ms_avr" || c.FunctionName.EndsWith("__delay_ms_avr")) 
                        && c.Args.Count == 1 && c.Args[0] is Constant msConst)
                    {
                        ulong cycles = (ulong)msConst.Value * (cfg.Frequency / 1000);
                        ulong loops = cycles / 6;
                        if (loops > 0) continue; 
                    }
                    if ((c.FunctionName == "_delay_us_avr" || c.FunctionName.EndsWith("__delay_us_avr")) 
                        && c.Args.Count == 1 && c.Args[0] is Constant usConst)
                    {
                        ulong cycles = (ulong)usConst.Value * (cfg.Frequency / 1000000);
                        ulong loops = cycles / 6;
                        if (loops > 0) continue; 
                    }
                    AddRef(c.FunctionName);
                }
                var valsToCheck = instr switch
                {
                    Binary b => new[] { b.Src1, b.Src2, b.Dst },
                    Copy cp => new[] { cp.Src, cp.Dst },
                    Return r => r.Value != null ? new[] { r.Value } : Array.Empty<Val>(),
                    Call cl => [.. cl.Args, cl.Dst],
                    _ => Array.Empty<Val>()
                };
                foreach (var v in valsToCheck)
                {
                    if (v is FunctionRef fr) AddRef(fr.FunctionName);
                }
            }
        }
        // ------------------------------------

        foreach (var func in program.Functions.Where(func => !func.IsInterrupt)
                     .Where(func => referencedFuncs.Contains(func.Name))
                     .Where(func => !func.IsInline || func.Name == "main"))
        {
            CompileFunction(func);
        }

        var optimized = AvrPeephole.Optimize(_assembly);
        foreach (var line in optimized)
            output.WriteLine(line.ToString());

        EmitFlashArrayPool(output);
        if (program.ExternSymbols.Contains("setjmp")) EmitExnRuntime(output, _usedExnCodes, cfg.Chip);
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
            Emit("CLR", "R1");
            Emit("LDI", "R16", "hi8(0x08FF)");
            Emit("OUT", "0x3E", "R16");
            Emit("LDI", "R16", "lo8(0x08FF)");
            Emit("OUT", "0x3D", "R16");
            Emit("LDI", "R28", "lo8(_stack_base)");
            Emit("LDI", "R29", "hi8(_stack_base)");
            if (_bssSize > 0)
            {
                var bssLoop = MakeLabel("L_BSS_LOOP");
                var bssEnd  = MakeLabel("L_BSS_END");
                Emit("LDI", "R26", "lo8(_stack_base)");
                Emit("LDI", "R27", "hi8(_stack_base)");
                Emit("LDI", "R30", "lo8(_bss_end)");
                Emit("LDI", "R31", "hi8(_bss_end)");
                Emit("CP",  "R26", "R30");
                Emit("CPC", "R27", "R31");
                Emit("BREQ", bssEnd);
                EmitLabel(bssLoop);
                Emit("ST", "X+", "R1");
                Emit("CP",  "R26", "R30");
                Emit("CPC", "R27", "R31");
                Emit("BRNE", bssLoop);
                EmitLabel(bssEnd);
            }
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
                    int pSize = p32 ? 4 : (p16 ? 2 : 1);
                    bool nearY = off + (pSize - 1) < 64;
                    if (nearY)
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
                    else
                    {
                        var abs = 0x0100 + off;
                        Emit("STS", $"0x{abs:X4}", aR);
                        if (p16 || p32) Emit("STS", $"0x{abs + 1:X4}", GetHighReg(aR));
                        if (p32)
                        {
                            string aR2 = k == 0 ? "R22" : $"R{int.Parse(aR[1..]) + 2}";
                            string aR3 = k == 0 ? "R23" : $"R{int.Parse(aR[1..]) + 3}";
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
            case InlineAsm asm2:
                if (asm2.Operands == null || asm2.Operands.Count == 0)
                {
                    _assembly.Add(AvrAsmLine.MakeRaw(asm2.Code));
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
            case TryBegin tb: CompileTryBegin(tb); break;
            case RaiseExn re: CompileRaiseExn(re); break;
        }
    }

    private void CompileReturn(Return r)
    {
        if (r.Value is not NoneVal)
        {
            var returnType = _currentFunction?.ReturnType ?? GetValType(r.Value);
            LoadIntoReg(r.Value, "R24", returnType);
        }

        if (!(_currentFunction?.IsNaked ?? false))
            Emit("RET");
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
        if (type.SizeOf() == 4) { Emit("OR", "R24", "R25"); Emit("OR", "R24", "R22"); Emit("OR", "R24", "R23"); }
        // OR R24, R25 already sets the Z flag for 16-bit values; no separate TST needed.
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
            // __fp_cmp(arg0, arg1): returns 0xFF if arg0 < arg1, 0x00 if equal, 0x01 if arg0 > arg1
            LoadFloatArg(src1, false);  // arg0 → R22:R23:R24:R25
            LoadFloatArg(src2, true);   // arg1 → R18:R19:R20:R21
            Emit("CALL", "__fp_cmp");
            // Map AVR integer branch to __fp_cmp result check
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
                // For uint32: a > val rewrites as a >= val+1.
                // Guard against overflow: val+1 must fit in a non-negative int.
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
        Console.WriteLine($"[INFO] [AVR-DEBUG] CompileCall: {call.FunctionName} args={call.Args.Count} isConst={(call.Args.Count > 0 ? call.Args[0] is Constant : false)}");
        if ((call.FunctionName == "_delay_ms_avr" || call.FunctionName.EndsWith("__delay_ms_avr")) && call.Args.Count == 1 && call.Args[0] is Constant msConst)
        {
            ulong cycles = (ulong)msConst.Value * (cfg.Frequency / 1000);
            ulong loops = cycles / 6;
            if (loops > 0)
            {
                EmitComment($"Inline delay for {msConst.Value} ms at {cfg.Frequency} Hz ({cycles} cycles)");
                Emit("LDI", "R18", $"{(loops & 0xFF)}");
                Emit("LDI", "R19", $"{(loops >> 8) & 0xFF}");
                Emit("LDI", "R20", $"{(loops >> 16) & 0xFF}");
                Emit("LDI", "R21", $"{(loops >> 24) & 0xFF}");
                string loopLabel = MakeLabel("L_DELAY");
                EmitLabel(loopLabel);
                Emit("SUBI", "R18", "1");
                Emit("SBCI", "R19", "0");
                Emit("SBCI", "R20", "0");
                Emit("SBCI", "R21", "0");
                Emit("BRNE", loopLabel);
            }
            return;
        }

        string[] argRegs = ["R24", "R22", "R20", "R18"];
        for (var k = 0; k < call.Args.Count && k < 4; k++)
        {
            // Use the declared parameter size when available so that constants
            // (e.g. Constant(-1) for an int16 param) are loaded with the correct
            // width instead of the size inferred from the constant's magnitude.
            var argType = GetValType(call.Args[k]);
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
        StoreRegInto("R24", call.Dst, dstType);
    }

    // Load a value into float.S calling-convention registers.
    // isArg1=false: arg0 → R22=B0, R23=B1, R24=B2, R25=B3
    // isArg1=true:  arg1 → R18=B0, R19=B1, R20=B2, R21=B3
    private void LoadFloatArg(Val v, bool isArg1)
    {
        DataType srcType = GetValType(v);

        if (v is FloatConstant fc)
        {
            uint bits = BitConverter.SingleToUInt32Bits((float)fc.Value);
            (string b0, string b1, string b2, string b3) = isArg1
                ? ("R18", "R19", "R20", "R21")
                : ("R22", "R23", "R24", "R25");
            Emit("LDI", b0, $"0x{bits & 0xFF:X2}");
            Emit("LDI", b1, $"0x{(bits >> 8) & 0xFF:X2}");
            Emit("LDI", b2, $"0x{(bits >> 16) & 0xFF:X2}");
            Emit("LDI", b3, $"0x{(bits >> 24) & 0xFF:X2}");
            return;
        }

        if (srcType != DataType.FLOAT)
        {
            // Integer → float: load int into R24(lo):R25(hi) then call __fp_int_to_float
            // Result lands in R22:R23:R24:R25 (float.S convention)
            LoadIntoReg(v, "R24", srcType);
            if (srcType.SizeOf() == 1)
            {
                if (IsSignedType(srcType)) { Emit("MOV", "R25", "R24"); Emit("LSL", "R25"); Emit("SBC", "R25", "R25"); }
                else Emit("CLR", "R25");
            }
            Emit("CALL", "__fp_int_to_float");
            if (isArg1)
            {
                Emit("MOV", "R18", "R22");
                Emit("MOV", "R19", "R23");
                Emit("MOV", "R20", "R24");
                Emit("MOV", "R21", "R25");
            }
            return;
        }

        // Float variable: load with existing convention (R24=B0, R25=B1, R22=B2, R23=B3)
        LoadIntoReg(v, "R24", DataType.FLOAT);
        if (!isArg1)
        {
            // XOR-swap R24↔R22 and R25↔R23 to get float.S arg0 layout
            Emit("EOR", "R24", "R22"); Emit("EOR", "R22", "R24"); Emit("EOR", "R24", "R22");
            Emit("EOR", "R25", "R23"); Emit("EOR", "R23", "R25"); Emit("EOR", "R25", "R23");
        }
        else
        {
            // Move to arg1 registers: R18=B0, R19=B1, R20=B2, R21=B3
            Emit("MOV", "R18", "R24");
            Emit("MOV", "R19", "R25");
            Emit("MOV", "R20", "R22");
            Emit("MOV", "R21", "R23");
        }
    }

    // Store the float.S result (R22=B0, R23=B1, R24=B2, R25=B3) into dst
    // using the compiler's normal memory convention (R24=B0).
    private void StoreFloatResult(Val dst)
    {
        Emit("EOR", "R24", "R22"); Emit("EOR", "R22", "R24"); Emit("EOR", "R24", "R22");
        Emit("EOR", "R25", "R23"); Emit("EOR", "R23", "R25"); Emit("EOR", "R25", "R23");
        StoreRegInto("R24", dst, DataType.FLOAT);
    }

    private void CompileFloatBinary(Binary b)
    {
        LoadFloatArg(b.Src1, false);  // arg0 → R22:R23:R24:R25
        LoadFloatArg(b.Src2, true);   // arg1 → R18:R19:R20:R21

        string fpFunc = b.Op switch
        {
            IrBinOp.Add                => "__fp_add",
            IrBinOp.Sub                => "__fp_sub",
            IrBinOp.Mul                => "__fp_mul",
            IrBinOp.Div or
            IrBinOp.FloorDiv           => "__fp_div",
            IrBinOp.Equal              => "__fp_eq",
            IrBinOp.NotEqual           => "__fp_ne",
            IrBinOp.LessThan           => "__fp_lt",
            IrBinOp.LessEqual          => "__fp_le",
            IrBinOp.GreaterThan        => "__fp_gt",
            IrBinOp.GreaterEqual       => "__fp_ge",
            _ => throw new NotSupportedException($"Float binary op {b.Op} not supported")
        };

        Emit("CALL", fpFunc);

        bool isCompare = b.Op is IrBinOp.Equal or IrBinOp.NotEqual
            or IrBinOp.LessThan or IrBinOp.LessEqual
            or IrBinOp.GreaterThan or IrBinOp.GreaterEqual;

        if (isCompare)
            StoreRegInto("R24", b.Dst, DataType.UINT8);
        else
            StoreFloatResult(b.Dst);
    }

    private void CompileCopy(Copy cp)
    {
        var srcType = GetValType(cp.Src);
        var dstType = GetValType(cp.Dst);

        // Float → int conversion
        if (srcType == DataType.FLOAT && dstType != DataType.FLOAT)
        {
            LoadFloatArg(cp.Src, false);          // float in R22:R23:R24:R25
            Emit("CALL", "__fp_float_to_int");    // result int16 in R24(lo):R25(hi)
            StoreRegInto("R24", cp.Dst, dstType.SizeOf() == 1 ? DataType.UINT8 : DataType.UINT16);
            return;
        }

        // Int → float conversion
        if (srcType != DataType.FLOAT && dstType == DataType.FLOAT && cp.Src is not FloatConstant)
        {
            LoadIntoReg(cp.Src, "R24", srcType);
            if (srcType.SizeOf() == 1)
            {
                if (IsSignedType(srcType)) { Emit("MOV", "R25", "R24"); Emit("LSL", "R25"); Emit("SBC", "R25", "R25"); }
                else Emit("CLR", "R25");
            }
            Emit("CALL", "__fp_int_to_float");
            // Result in R22:R23:R24:R25; swap to R24=B0 convention before storing
            Emit("EOR", "R24", "R22"); Emit("EOR", "R22", "R24"); Emit("EOR", "R24", "R22");
            Emit("EOR", "R25", "R23"); Emit("EOR", "R23", "R25"); Emit("EOR", "R25", "R23");
            StoreRegInto("R24", cp.Dst, DataType.FLOAT);
            return;
        }

        // Default path (includes FloatConstant → float variable)
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

        // Dispatch float operations to the soft-float runtime
        bool srcIsFloat = GetValType(b.Src1) == DataType.FLOAT || GetValType(b.Src2) == DataType.FLOAT
            || b.Src1 is FloatConstant || b.Src2 is FloatConstant;
        if (type == DataType.FLOAT || srcIsFloat)
        {
            CompileFloatBinary(b);
            return;
        }

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

    // Load one byte from a bytearray pointer parameter: R24 = ptr[index].
    // The pointer is a 16-bit value stored in the callee's stack frame.
    private void CompileBytearrayLoad(BytearrayLoad bl)
    {
        // Load the pointer (base address) from the stack slot into Z.
        if (_stackLayout.TryGetValue(bl.PtrName, out int ptrOffset))
        {
            Emit("LDD", "R30", $"Y+{ptrOffset}");
            Emit("LDD", "R31", $"Y+{ptrOffset + 1}");
        }
        else
        {
            // Pointer is in a register pair (e.g. R24:R25 as function parameter).
            if (_regLayout.TryGetValue(bl.PtrName, out string baseReg))
            {
                Emit("MOV", "R30", baseReg);
                Emit("MOV", "R31", GetHighReg(baseReg));
            }
            else
            {
                EmitComment("BytearrayLoad: pointer location unknown -- skip");
                return;
            }
        }

        // Add index to Z, then load the byte.
        if (bl.Index is Constant cIdx && cIdx.Value == 0)
        {
            Emit("LD", "R24", "Z");
        }
        else if (bl.Index is Constant cIdx2)
        {
            Emit("LDI", "R16", $"{cIdx2.Value}");
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
        // Load source value.
        LoadIntoReg(bs.Src, "R18", DataType.UINT8);

        // Load the pointer (base address) from the stack slot into Z.
        if (_stackLayout.TryGetValue(bs.PtrName, out int ptrOffset))
        {
            Emit("LDD", "R30", $"Y+{ptrOffset}");
            Emit("LDD", "R31", $"Y+{ptrOffset + 1}");
        }
        else if (_regLayout.TryGetValue(bs.PtrName, out string baseReg))
        {
            Emit("MOV", "R30", baseReg);
            Emit("MOV", "R31", GetHighReg(baseReg));
        }
        else
        {
            EmitComment("BytearrayStore: pointer location unknown -- skip");
            return;
        }

        // Add index to Z, then store.
        if (bs.Index is Constant cIdx && cIdx.Value == 0)
        {
            Emit("ST", "Z", "R18");
        }
        else if (bs.Index is Constant cIdx2)
        {
            Emit("LDI", "R16", $"{cIdx2.Value}");
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

    private void CompileTryBegin(TryBegin tb)
    {
        // Load address of jmpbuf into R25:R24 (avr-gcc arg0 convention).
        // jmpbuf is a stack-local variable; its address = RAMSTART + offset(jmpbuf).
        string jmpBufName = (tb.JmpBufVar as Variable)?.Name ?? "";
        if (!_stackLayout.TryGetValue(jmpBufName, out int jmpBufOffset))
            throw new Exception($"jmpbuf variable '{jmpBufName}' not found in stack layout");

        int jmpBufAddr = 0x0100 + jmpBufOffset;
        Emit("LDI", "R24", $"lo8({jmpBufAddr})");
        Emit("LDI", "R25", $"hi8({jmpBufAddr})");

        // Store the jmpbuf pointer in __pymcu_active_jmpbuf (global 2-byte SRAM slot).
        if (_stackLayout.TryGetValue("__pymcu_active_jmpbuf", out int activeOffset))
        {
            int activeAddr = 0x0100 + activeOffset;
            Emit("STS", activeAddr.ToString(), "R24");
            Emit("STS", (activeAddr + 1).ToString(), "R25");
        }

        // Call _setjmp(jmpbuf). R25:R24 already loaded.
        Emit("CALL", "setjmp");

        // If _setjmp returns != 0 (longjmp fired), jump to catch label.
        // Store the exception code (R24) into exnCodeVar first.
        string exnCodeName = (tb.ExnCodeVar as Variable)?.Name ?? "";
        if (_stackLayout.TryGetValue(exnCodeName, out int exnOffset))
        {
            bool nearY = exnOffset < 64;
            if (nearY)
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
        // Load exception code into R22 (arg1 for longjmp).
        LoadIntoReg(re.Code, "R22", DataType.UINT8);
        Emit("CLR", "R23");

        // Load __pymcu_active_jmpbuf pointer into R24:R25 (arg0).
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

        // If pointer is null (0), call __pymcu_unhandled_exn(code).
        // Otherwise call longjmp(jmpbuf, code). longjmp never returns.
        string noHandlerLabel = $"L_no_handler_{_labelCounter++}";
        Emit("MOV", "R16", "R24");
        Emit("OR", "R16", "R25");
        Emit("TST", "R16");
        Emit("BREQ", noHandlerLabel);
        Emit("CALL", "longjmp");
        EmitLabel(noHandlerLabel);
        Emit("CALL", "__pymcu_unhandled_exn");
    }
}