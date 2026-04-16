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

namespace PyMCU.Backend.Targets.PIC14;

public class PIC14CodeGen : CodeGen
{
    // Exposed for ArchStrategy subclasses
    internal readonly DeviceConfig Config;
    private readonly ArchStrategy _strategy;

    private List<PIC14AsmLine> _assembly = new();
    private Dictionary<string, int> _symbolTable = new();
    private Dictionary<string, int> _stackLayout = new();
    private Dictionary<string, int> _varSizes = new();
    private int _ramHead;
    private int _labelCounter;
    private bool _usesFloat;
    private int _currentBank = -1;
    private bool _currentBlockTerminated;
    private string _currentFunctionName = "";

    public PIC14CodeGen(DeviceConfig cfg)
    {
        Config = cfg;
        _ramHead = 0x20;
        _labelCounter = 0;

        // Strategy Selection Logic
        // Strict check against config.Arch OR check if TargetChip implies PIC14E
        bool isEnhanced = cfg.Arch == "pic14e" ||
                          (cfg.Arch.StartsWith("pic16f1") && cfg.Arch.Length > 9);

        _strategy = isEnhanced ? new PIC14EStrategy(this) : new PIC14Strategy(this);
    }

    private string MakeLabel(string prefix) => $"{prefix}_{_labelCounter++}";

    // Parse a hex address string that may or may not have a leading "0x"/"0X" prefix.
    private static int ParseHexAddr(string s)
    {
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return Convert.ToInt32(s.Substring(2), 16);
        return Convert.ToInt32(s, 16);
    }

    // --- Emission Helpers (internal — used by ArchStrategy) ---

    internal void Emit(string mnemonic) =>
        _assembly.Add(PIC14AsmLine.MakeInstruction(mnemonic));

    internal void Emit(string mnemonic, string op1) =>
        _assembly.Add(PIC14AsmLine.MakeInstruction(mnemonic, op1));

    internal void Emit(string mnemonic, string op1, string op2) =>
        _assembly.Add(PIC14AsmLine.MakeInstruction(mnemonic, op1, op2));

    internal void EmitLabel(string label) =>
        _assembly.Add(PIC14AsmLine.MakeLabel(label));

    internal void EmitComment(string comment) =>
        _assembly.Add(PIC14AsmLine.MakeComment(comment));

    internal void EmitRaw(string text) =>
        _assembly.Add(PIC14AsmLine.MakeRaw(text));

    // --- CodeGen abstract overrides ---

    public override void EmitContextSave() => _strategy.EmitContextSave();
    public override void EmitContextRestore() => _strategy.EmitContextRestore();
    public override void EmitInterruptReturn() => _strategy.EmitInterruptReturn();

    // --- Memory & Bank Management ---

    private string GetOrAllocVariable(string name)
    {
        if (!_symbolTable.ContainsKey(name))
        {
            _symbolTable[name] = _ramHead++;
            EmitRaw($"{name} EQU 0x{_symbolTable[name]:X2}");
        }

        return name;
    }

    private string ResolveAddress(Val val)
    {
        if (val is Constant c)
            return $"0x{c.Value & 0xFF:X2}";
        if (val is MemoryAddress mem)
            return $"0x{mem.Address:X2}";

        string name = val switch
        {
            Variable v => v.Name,
            Temporary t => t.Name,
            _ => ""
        };

        if (_stackLayout.ContainsKey(name))
            return name;

        return GetOrAllocVariable(name);
    }

    private void SelectBank(string operand)
    {
        int addr = -1;

        try
        {
            if (operand.Length > 2 && operand.StartsWith("0x"))
            {
                addr = ParseHexAddr(operand);
            }
            else if (_stackLayout.ContainsKey(operand))
            {
                addr = 0x20 + _stackLayout[operand]; // Assume Bank 0 area
            }
            else if (_symbolTable.ContainsKey(operand))
            {
                addr = _symbolTable[operand];
            }
            else
            {
                // Handle "name+N" suffix (e.g. "nco_init.inc_val+1")
                int plusPos = operand.IndexOf('+');
                if (plusPos >= 0)
                {
                    string baseOp = operand.Substring(0, plusPos);
                    int offset = int.Parse(operand.Substring(plusPos + 1));
                    if (_stackLayout.ContainsKey(baseOp))
                        addr = 0x20 + _stackLayout[baseOp] + offset;
                    else if (_symbolTable.ContainsKey(baseOp))
                        addr = _symbolTable[baseOp] + offset;
                }
            }
        }
        catch
        {
        }

        if (addr != -1)
        {
            int newBank = (addr >> 7) & 0x7F;
            if (_currentBank == newBank) return;
            _strategy.EmitBankSelect(newBank);
            _currentBank = newBank;
        }
        else
        {
            _currentBank = -1; // Invalidate
        }
    }

    // --- Config directives ---

    internal void EmitConfigDirectives()
    {
        if (Config.Fuses == null || Config.Fuses.Count == 0) return;

        string configLine = "\t__CONFIG";
        bool first = true;
        foreach (var kv in Config.Fuses)
        {
            if (!first) configLine += " &";
            if (kv.Value == "ON" || kv.Value == "1" || kv.Value == "TRUE")
                configLine += " " + kv.Key;
            else if (kv.Value == "OFF" || kv.Value == "0" || kv.Value == "FALSE")
                configLine += " " + kv.Key;
            else
                configLine += " " + kv.Value;
            first = false;
        }

        EmitRaw(configLine);
    }

    // --- Main Compile entry point ---

    public override void Compile(ProgramIR program, TextWriter output)
    {
        _assembly.Clear();

        var allocator = new StackAllocator();
        var (offsets, totalSize) = allocator.Allocate(program);

        // Pre-scan for floats
        _usesFloat = false;
        foreach (var func in program.Functions)
        {
            foreach (var instr in func.Body)
            {
                if (instr is Binary bin)
                {
                    bool IsFloatVal(Val v) => v switch
                    {
                        Variable vv => vv.Type == DataType.FLOAT,
                        Temporary tt => tt.Type == DataType.FLOAT,
                        _ => false
                    };

                    if (IsFloatVal(bin.Dst) || IsFloatVal(bin.Src1) || IsFloatVal(bin.Src2))
                    {
                        _usesFloat = true;
                        break;
                    }
                }
            }

            if (_usesFloat) break;
        }

        _stackLayout = offsets;
        _varSizes = allocator.VariableSizes;

        // Advance ram_head past the stack (and float workspace) to prevent
        // collisions with dynamically-allocated variables (delay counters, etc.)
        _ramHead = 0x20 + totalSize;
        if (_usesFloat) _ramHead += 12; // __FP_A(4) + __FP_B(4) + __FP_C(4)

        // 1. Preamble FIRST (LIST, #include, errorlevel, CONFIG)
        _strategy.EmitPreamble();
        EmitRaw("");

        // 2. Stack layout (CBLOCK/ENDC)
        if (_stackLayout.Count > 0 || _usesFloat)
        {
            EmitComment("--- Compiled Stack (Overlays) ---");
            EmitRaw("\tCBLOCK 0x20");
            EmitRaw($"_stack_base: {totalSize}");
            if (_usesFloat)
            {
                EmitRaw("__FP_A: 4");
                EmitRaw("__FP_B: 4");
                EmitRaw("__FP_C: 4");
            }

            EmitRaw("\tENDC");
            EmitRaw("");
        }

        // 3. Variable offsets (EQU)
        if (_stackLayout.Count > 0)
        {
            EmitComment("--- Variable Offsets ---");
            foreach (var kv in _stackLayout)
                EmitRaw($"{kv.Key} EQU _stack_base + {kv.Value}");
            EmitRaw("");
        }

        // 4. Reset vector
        int resetAddr = Config.ResetVector >= 0 ? Config.ResetVector : 0x0000;
        EmitComment("--- Reset Vector ---");
        EmitRaw($"\tORG 0x{resetAddr:X4}");
        Emit("GOTO", "main");
        EmitRaw("");

        // 5. Interrupt vector (PIC14 only supports 0x04)
        int intAddr = Config.InterruptVector >= 0 ? Config.InterruptVector : 0x0004;

        Function? isr0x04 = null;
        foreach (var func in program.Functions)
        {
            if (func.IsInterrupt)
            {
                if (func.InterruptVector != 0x04 && func.InterruptVector != 0)
                    throw new InvalidOperationException(
                        $"PIC14 architecture does not support interrupt vector " +
                        $"0x{func.InterruptVector:X2}. Only 0x04 is supported.");

                if (isr0x04 != null)
                    throw new InvalidOperationException(
                        "Multiple interrupt handlers defined for vector 0x04.");
                isr0x04 = func;
            }
        }

        EmitComment("--- Interrupt Vector ---");
        if (isr0x04 != null)
        {
            EmitRaw($"\tORG 0x{intAddr:X4}");
            EmitLabel("__interrupt");

            EmitContextSave();

            _currentFunctionName = isr0x04.Name;
            _currentBank = -1;
            _currentBlockTerminated = false;

            foreach (var instr in isr0x04.Body)
            {
                if (_currentBlockTerminated)
                {
                    if (instr is Label)
                    {
                        _currentBlockTerminated = false;
                        CompileInstruction(instr);
                    }

                    continue;
                }

                if (instr is Return)
                {
                    EmitContextRestore();
                    EmitInterruptReturn();
                    _currentBlockTerminated = true;
                    continue;
                }

                CompileInstruction(instr);
            }

            if (!_currentBlockTerminated)
            {
                EmitContextRestore();
                EmitInterruptReturn();
            }
        }
        else
        {
            // Dummy interrupt handler
            EmitRaw($"\tORG 0x{intAddr:X4}");
            EmitLabel("__interrupt");
            Emit("RETFIE");
        }

        // Emit non-main, non-interrupt functions
        foreach (var func in program.Functions)
        {
            if (func.IsInterrupt) continue;
            if (func.IsInline && func.Name != "main") continue;
            if (func.Name == "main") continue;
            CompileFunction(func);
        }

        // Emit main at the end
        foreach (var func in program.Functions)
        {
            if (func.Name == "main")
            {
                CompileFunction(func);
                break;
            }
        }

        if (_usesFloat)
            EmitRaw("#include \"float.inc\"");

        EmitRaw("\tEND");

        // Peephole optimization pass
        _assembly = PIC14Peephole.Optimize(_assembly);

        foreach (var line in _assembly)
            output.WriteLine(line.ToString());
    }

    // --- Function compilation ---

    private void CompileFunction(Function func)
    {
        EmitLabel(func.Name);
        _currentBank = -1;
        _currentBlockTerminated = false;

        foreach (var instr in func.Body)
        {
            if (_currentBlockTerminated)
            {
                if (instr is Label)
                {
                    _currentBlockTerminated = false;
                    CompileInstruction(instr);
                }

                continue;
            }

            CompileInstruction(instr);
        }
    }

    private void CompileInstruction(Instruction instr)
    {
        switch (instr)
        {
            case Return arg: CompileVariant(arg); break;
            case Copy arg: CompileVariant(arg); break;
            case LoadIndirect arg: CompileVariant(arg); break;
            case StoreIndirect arg: CompileVariant(arg); break;
            case Unary arg: CompileVariant(arg); break;
            case Binary arg: CompileVariant(arg); break;
            case BitSet arg: CompileVariant(arg); break;
            case BitClear arg: CompileVariant(arg); break;
            case BitCheck arg: CompileVariant(arg); break;
            case BitWrite arg: CompileVariant(arg); break;
            case AugAssign arg: CompileVariant(arg); break;
            case InlineAsm arg: CompileVariant(arg); break;
            case DebugLine arg: CompileVariant(arg); break;
            case Label arg: CompileVariant(arg); break;
            case Jump arg: CompileVariant(arg); break;
            case JumpIfZero arg: CompileVariant(arg); break;
            case JumpIfNotZero arg: CompileVariant(arg); break;
            case JumpIfEqual arg: CompileVariant(arg); break;
            case JumpIfNotEqual arg: CompileVariant(arg); break;
            case JumpIfLessThan arg: CompileVariant(arg); break;
            case JumpIfLessOrEqual arg: CompileVariant(arg); break;
            case JumpIfGreaterThan arg: CompileVariant(arg); break;
            case JumpIfGreaterOrEqual arg: CompileVariant(arg); break;
            case JumpIfBitSet arg: CompileVariant(arg); break;
            case JumpIfBitClear arg: CompileVariant(arg); break;
            case Call arg: CompileVariant(arg); break;
            // AVR-only; no-op here
            case UARTSendString: break;
            case ArrayLoad: break;
            case ArrayStore: break;
            default:
                throw new NotSupportedException($"PIC14CodeGen: unsupported instruction {instr.GetType().Name}");
        }
    }

    // --- Logic Helpers ---

    private void LoadIntoW(Val val)
    {
        if (val is NoneVal)
        {
            Emit("MOVLW", "0x00");
            return;
        }

        if (val is Constant c)
        {
            Emit("MOVLW", $"0x{c.Value & 0xFF:X2}");
        }
        else
        {
            string op = ResolveAddress(val);
            SelectBank(op);
            Emit("MOVF", op, "W");
        }
    }

    private void StoreWInto(Val val)
    {
        if (val is NoneVal) return;
        string op = ResolveAddress(val);
        SelectBank(op);
        Emit("MOVWF", op);
    }

    // --- Instruction Visitors ---

    private void CompileVariant(Return arg)
    {
        if (arg.Value is not NoneVal)
            LoadIntoW(arg.Value);
        Emit("RETURN");
        _currentBlockTerminated = true;
    }

    private void CompileVariant(Copy arg)
    {
        int size = 1;
        string name = "";
        if (arg.Dst is Variable dv) name = dv.Name;
        else if (arg.Dst is Temporary dt) name = dt.Name;
        else if (arg.Dst is MemoryAddress dma)
            size = (dma.Type == DataType.UINT16) ? 2 : 1;

        if (!string.IsNullOrEmpty(name) && _varSizes.ContainsKey(name))
            size = _varSizes[name];

        if (size == 1)
        {
            LoadIntoW(arg.Src);
            StoreWInto(arg.Dst);
            return;
        }

        if (size == 2)
        {
            string dstLo = ResolveAddress(arg.Dst);
            string dstHi = dstLo.StartsWith("0x")
                ? $"0x{ParseHexAddr(dstLo) + 1:X2}"
                : dstLo + "+1";

            if (arg.Src is Constant sc)
            {
                int val = sc.Value;
                Emit("MOVLW", $"0x{val & 0xFF:X2}");
                SelectBank(dstLo);
                Emit("MOVWF", dstLo);
                Emit("MOVLW", $"0x{(val >> 8) & 0xFF:X2}");
                SelectBank(dstHi);
                Emit("MOVWF", dstHi);
            }
            else
            {
                string srcLo = ResolveAddress(arg.Src);
                string srcHi = srcLo.StartsWith("0x")
                    ? $"0x{ParseHexAddr(srcLo) + 1:X2}"
                    : srcLo + "+1";

                SelectBank(srcLo);
                Emit("MOVF", srcLo, "W");
                SelectBank(dstLo);
                Emit("MOVWF", dstLo);

                SelectBank(srcHi);
                Emit("MOVF", srcHi, "W");
                SelectBank(dstHi);
                Emit("MOVWF", dstHi);
            }

            return;
        }

        if (size == 4)
        {
            if (arg.Src is Constant sc4)
            {
                int val = sc4.Value;
                string dstBase = ResolveAddress(arg.Dst);
                for (int i = 0; i < 4; i++)
                {
                    int byteVal = (val >> (i * 8)) & 0xFF;
                    Emit("MOVLW", $"0x{byteVal:X2}");
                    string dstByte = i == 0
                        ? dstBase
                        : dstBase.StartsWith("0x")
                            ? $"0x{ParseHexAddr(dstBase) + i:X2}"
                            : dstBase + "+" + i;
                    SelectBank(dstByte);
                    Emit("MOVWF", dstByte);
                }

                return;
            }

            if (arg.Src is Variable)
            {
                string srcBase = ResolveAddress(arg.Src);
                string dstBase = ResolveAddress(arg.Dst);
                for (int i = 0; i < 4; i++)
                {
                    string srcByte = i == 0
                        ? srcBase
                        : srcBase.StartsWith("0x")
                            ? $"0x{ParseHexAddr(srcBase) + i:X2}"
                            : srcBase + "+" + i;
                    string dstByte = i == 0
                        ? dstBase
                        : dstBase.StartsWith("0x")
                            ? $"0x{ParseHexAddr(dstBase) + i:X2}"
                            : dstBase + "+" + i;
                    SelectBank(srcByte);
                    Emit("MOVF", srcByte, "W");
                    SelectBank(dstByte);
                    Emit("MOVWF", dstByte);
                }

                return;
            }
        }

        throw new NotSupportedException("PIC14: Copy only supports 1, 2, 4 bytes");
    }

    private void CompileVariant(LoadIndirect arg)
    {
        // PIC14 Indirect Addressing: FSR / INDF
        if (arg.SrcPtr is Constant cp)
        {
            Emit("MOVLW", $"0x{cp.Value & 0xFF:X2}");
            Emit("MOVWF", "FSR");
        }
        else
        {
            string ptrAddr = ResolveAddress(arg.SrcPtr);
            SelectBank(ptrAddr);
            Emit("MOVF", ptrAddr, "W");
            Emit("MOVWF", "FSR");
        }

        Emit("MOVF", "INDF", "W");
        StoreWInto(arg.Dst);
    }

    private void CompileVariant(StoreIndirect arg)
    {
        if (arg.DstPtr is Constant cp)
        {
            Emit("MOVLW", $"0x{cp.Value & 0xFF:X2}");
            Emit("MOVWF", "FSR");
        }
        else
        {
            string ptrAddr = ResolveAddress(arg.DstPtr);
            SelectBank(ptrAddr);
            Emit("MOVF", ptrAddr, "W");
            Emit("MOVWF", "FSR");
        }

        LoadIntoW(arg.Src);
        Emit("MOVWF", "INDF");
    }

    private void CompileVariant(Unary arg)
    {
        LoadIntoW(arg.Src);
        switch (arg.Op)
        {
            case IrUnOp.BitNot:
                Emit("XORLW", "0xFF");
                break;
            case IrUnOp.Not:
                Emit("XORLW", "1");
                Emit("ANDLW", "1");
                break;
            case IrUnOp.Neg:
                Emit("SUBLW", "0");
                break;
            default:
                throw new NotSupportedException($"PIC14: Unary op {arg.Op} not supported");
        }

        StoreWInto(arg.Dst);
    }

    private void CompileVariant(Binary arg)
    {
        // Determine operation size
        int GetValSize(Val val)
        {
            if (val is Variable v && _varSizes.ContainsKey(v.Name)) return _varSizes[v.Name];
            if (val is Temporary t && _varSizes.ContainsKey(t.Name)) return _varSizes[t.Name];
            if (val is MemoryAddress m && m.Type == DataType.UINT16) return 2;
            if (val is Constant c && (c.Value > 255 || c.Value < 0)) return 2;
            return 1;
        }

        bool isComparison = arg.Op switch
        {
            IrBinOp.Equal or IrBinOp.NotEqual or IrBinOp.LessThan or
                IrBinOp.LessEqual or IrBinOp.GreaterThan or IrBinOp.GreaterEqual => true,
            _ => false
        };

        int size = isComparison
            ? Math.Max(GetValSize(arg.Src1), GetValSize(arg.Src2))
            : GetValSize(arg.Dst);

        // Check for Float
        bool IsFloat(Val val) => val switch
        {
            Variable v => v.Type == DataType.FLOAT,
            Temporary t => t.Type == DataType.FLOAT,
            _ => false
        };

        if (IsFloat(arg.Dst) || IsFloat(arg.Src1) || IsFloat(arg.Src2))
        {
            if (arg.Op == IrBinOp.Add)
            {
                string dstAddr = ResolveAddress(arg.Dst);
                CompileVariant(new Copy(arg.Src1, arg.Dst));
                string src2Addr = ResolveAddress(arg.Src2);
                EmitFloatAdd(dstAddr, src2Addr);
                return;
            }

            throw new NotSupportedException("PIC14: Float op not supported (only Add)");
        }

        // --- 16-bit Implementation ---
        if (size == 2)
        {
            if (!isComparison)
            {
                CompileVariant(new Copy(arg.Src1, arg.Dst));
                switch (arg.Op)
                {
                    case IrBinOp.Add:
                    case IrBinOp.Sub:
                    case IrBinOp.BitAnd:
                    case IrBinOp.BitOr:
                    case IrBinOp.BitXor:
                    case IrBinOp.LShift:
                    case IrBinOp.RShift:
                        CompileVariant(new AugAssign(arg.Op, arg.Dst, arg.Src2));
                        break;
                    default:
                        throw new NotSupportedException("PIC14: 16-bit Binary Op not supported");
                }

                return;
            }
            else
            {
                // 16-bit Comparison -> 8-bit boolean result (0 or 1)
                string dstAddr = ResolveAddress(arg.Dst);
                SelectBank(dstAddr);
                Emit("CLRF", dstAddr);

                void LoadByte(Val v, bool high)
                {
                    if (v is Constant c)
                    {
                        int bval = high ? ((c.Value >> 8) & 0xFF) : (c.Value & 0xFF);
                        Emit("MOVLW", $"0x{bval:X2}");
                    }
                    else
                    {
                        string addr = ResolveAddress(v);
                        if (high)
                            addr = addr.StartsWith("0x")
                                ? $"0x{ParseHexAddr(addr) + 1:X2}"
                                : addr + "+1";
                        SelectBank(addr);
                        Emit("MOVF", addr, "W");
                    }
                }

                string GetAddr(Val v, bool high)
                {
                    string addr = ResolveAddress(v);
                    if (high)
                        addr = addr.StartsWith("0x")
                            ? $"0x{ParseHexAddr(addr) + 1:X2}"
                            : addr + "+1";
                    return addr;
                }

                string labelTrue = MakeLabel("cmp_true");
                string labelEnd = MakeLabel("cmp_end");

                if (arg.Op == IrBinOp.Equal)
                {
                    LoadByte(arg.Src2, true);
                    string src1Hi = GetAddr(arg.Src1, true);
                    if (arg.Src1 is Constant sc1)
                        Emit("XORLW", $"0x{(sc1.Value >> 8) & 0xFF:X2}");
                    else
                    {
                        SelectBank(src1Hi);
                        Emit("XORWF", src1Hi, "W");
                    }

                    Emit("BTFSS", "STATUS", "2");
                    Emit("GOTO", labelEnd);

                    LoadByte(arg.Src2, false);
                    string src1Lo = GetAddr(arg.Src1, false);
                    if (arg.Src1 is Constant sc1b)
                        Emit("XORLW", $"0x{sc1b.Value & 0xFF:X2}");
                    else
                    {
                        SelectBank(src1Lo);
                        Emit("XORWF", src1Lo, "W");
                    }

                    Emit("BTFSS", "STATUS", "2");
                    Emit("GOTO", labelEnd);

                    SelectBank(dstAddr);
                    Emit("INCF", dstAddr, "F");
                    EmitLabel(labelEnd);
                    return;
                }

                if (arg.Op == IrBinOp.NotEqual)
                {
                    LoadByte(arg.Src2, true);
                    string src1Hi = GetAddr(arg.Src1, true);
                    if (arg.Src1 is Constant sc1)
                        Emit("XORLW", $"0x{(sc1.Value >> 8) & 0xFF:X2}");
                    else
                    {
                        SelectBank(src1Hi);
                        Emit("XORWF", src1Hi, "W");
                    }

                    Emit("BTFSS", "STATUS", "2");
                    Emit("GOTO", labelTrue);

                    LoadByte(arg.Src2, false);
                    string src1Lo = GetAddr(arg.Src1, false);
                    if (arg.Src1 is Constant sc1b)
                        Emit("XORLW", $"0x{sc1b.Value & 0xFF:X2}");
                    else
                    {
                        SelectBank(src1Lo);
                        Emit("XORWF", src1Lo, "W");
                    }

                    Emit("BTFSC", "STATUS", "2");
                    Emit("GOTO", labelEnd);

                    EmitLabel(labelTrue);
                    SelectBank(dstAddr);
                    Emit("INCF", dstAddr, "F");
                    EmitLabel(labelEnd);
                    return;
                }

                // <, <=, >, >= (Unsigned assumed)
                LoadByte(arg.Src2, true);
                string s1Hi = GetAddr(arg.Src1, true);
                if (arg.Src1 is Constant cHi)
                    Emit("SUBLW", $"0x{(cHi.Value >> 8) & 0xFF:X2}");
                else
                {
                    SelectBank(s1Hi);
                    Emit("SUBWF", s1Hi, "W");
                }

                string lblChk = MakeLabel("cmp_chk");
                Emit("BTFSS", "STATUS", "2");
                Emit("GOTO", lblChk);

                LoadByte(arg.Src2, false);
                string s1Lo = GetAddr(arg.Src1, false);
                if (arg.Src1 is Constant cLo)
                    Emit("SUBLW", $"0x{cLo.Value & 0xFF:X2}");
                else
                {
                    SelectBank(s1Lo);
                    Emit("SUBWF", s1Lo, "W");
                }

                EmitLabel(lblChk);

                if (arg.Op == IrBinOp.LessThan)
                {
                    Emit("BTFSS", "STATUS", "0");
                    Emit("GOTO", labelTrue);
                }
                else if (arg.Op == IrBinOp.GreaterEqual)
                {
                    Emit("BTFSC", "STATUS", "0");
                    Emit("GOTO", labelTrue);
                }
                else if (arg.Op == IrBinOp.GreaterThan)
                {
                    Emit("BTFSS", "STATUS", "0");
                    Emit("GOTO", labelEnd);
                    Emit("BTFSS", "STATUS", "2");
                    Emit("GOTO", labelTrue);
                }
                else if (arg.Op == IrBinOp.LessEqual)
                {
                    Emit("BTFSS", "STATUS", "0");
                    Emit("GOTO", labelTrue);
                    Emit("BTFSC", "STATUS", "2");
                    Emit("GOTO", labelTrue);
                }

                Emit("GOTO", labelEnd);
                EmitLabel(labelTrue);
                SelectBank(dstAddr);
                Emit("INCF", dstAddr, "F");
                EmitLabel(labelEnd);
                return;
            }
        }

        // --- 8-Bit Comparisons with Optimization ---
        if (isComparison && size == 1)
        {
            string dstAddr = ResolveAddress(arg.Dst);
            SelectBank(dstAddr);
            Emit("CLRF", dstAddr);

            string labelTrue = MakeLabel("cmp_true");
            string labelEnd = MakeLabel("cmp_end");

            // Optimization: RHS is a literal constant
            if (arg.Src2 is Constant c2Lit &&
                (arg.Src1 is Variable || arg.Src1 is Temporary || arg.Src1 is MemoryAddress))
            {
                int k = c2Lit.Value & 0xFF;
                string srcAddr = ResolveAddress(arg.Src1);
                SelectBank(srcAddr);
                Emit("MOVF", srcAddr, "W");

                if (arg.Op == IrBinOp.Equal)
                {
                    Emit("XORLW", $"0x{k:X2}");
                    Emit("BTFSC", "STATUS", "2");
                    Emit("GOTO", labelTrue);
                    Emit("GOTO", labelEnd);
                }
                else if (arg.Op == IrBinOp.NotEqual)
                {
                    Emit("XORLW", $"0x{k:X2}");
                    Emit("BTFSS", "STATUS", "2");
                    Emit("GOTO", labelTrue);
                    Emit("GOTO", labelEnd);
                }
                else if (arg.Op == IrBinOp.GreaterThan)
                {
                    Emit("SUBLW", $"0x{k:X2}");
                    Emit("BTFSS", "STATUS", "0");
                    Emit("GOTO", labelTrue);
                    Emit("GOTO", labelEnd);
                }
                else if (arg.Op == IrBinOp.LessEqual)
                {
                    Emit("SUBLW", $"0x{k:X2}");
                    Emit("BTFSC", "STATUS", "0");
                    Emit("GOTO", labelTrue);
                    Emit("GOTO", labelEnd);
                }
                else if (arg.Op == IrBinOp.LessThan)
                {
                    if (k == 0)
                        Emit("GOTO", labelEnd);
                    else
                    {
                        Emit("SUBLW", $"0x{k - 1:X2}");
                        Emit("BTFSC", "STATUS", "0");
                        Emit("GOTO", labelTrue);
                        Emit("GOTO", labelEnd);
                    }
                }
                else if (arg.Op == IrBinOp.GreaterEqual)
                {
                    if (k == 0)
                        Emit("GOTO", labelTrue);
                    else
                    {
                        Emit("SUBLW", $"0x{k - 1:X2}");
                        Emit("BTFSS", "STATUS", "0");
                        Emit("GOTO", labelTrue);
                        Emit("GOTO", labelEnd);
                    }
                }

                EmitLabel(labelTrue);
                SelectBank(dstAddr);
                Emit("INCF", dstAddr, "F");
                EmitLabel(labelEnd);
                return;
            }

            // Generic 8-bit comparison
            LoadIntoW(arg.Src2);
            string addr1 = ResolveAddress(arg.Src1);
            SelectBank(addr1);
            Emit("SUBWF", addr1, "W");

            switch (arg.Op)
            {
                case IrBinOp.Equal:
                    Emit("BTFSC", "STATUS", "2");
                    break;
                case IrBinOp.NotEqual:
                    Emit("BTFSS", "STATUS", "2");
                    break;
                case IrBinOp.LessThan:
                    Emit("BTFSS", "STATUS", "0");
                    break;
                case IrBinOp.GreaterEqual:
                    Emit("BTFSC", "STATUS", "0");
                    break;
                case IrBinOp.GreaterThan:
                {
                    string lblSkip = MakeLabel("L_GT");
                    Emit("BTFSS", "STATUS", "0");
                    Emit("GOTO", lblSkip);
                    Emit("BTFSC", "STATUS", "2");
                    Emit("GOTO", lblSkip);
                    Emit("INCF", dstAddr, "F");
                    EmitLabel(lblSkip);
                    return;
                }
                case IrBinOp.LessEqual:
                {
                    string lblSet = MakeLabel("L_LE");
                    string lblSkip = MakeLabel("L_LES");
                    Emit("BTFSS", "STATUS", "0");
                    Emit("GOTO", lblSet);
                    Emit("BTFSC", "STATUS", "2");
                    Emit("GOTO", lblSkip);
                    EmitLabel(lblSet);
                    Emit("INCF", dstAddr, "F");
                    EmitLabel(lblSkip);
                    return;
                }
                default: break;
            }

            Emit("INCF", dstAddr, "F");
            EmitLabel(labelEnd);
            return;
        }

        if (!isComparison)
        {
            // --- Shift operations ---
            if (arg.Op == IrBinOp.LShift || arg.Op == IrBinOp.RShift)
            {
                string rotateOp = (arg.Op == IrBinOp.LShift) ? "RLF" : "RRF";

                if (arg.Src2 is Constant shiftC)
                {
                    int rawShift = shiftC.Value;
                    int byteOffset = rawShift / 8;
                    int bitShift = rawShift % 8;

                    if (arg.Op == IrBinOp.RShift && byteOffset > 0)
                    {
                        string srcAddr = ResolveAddress(arg.Src1);
                        string srcHi = srcAddr.StartsWith("0x")
                            ? $"0x{ParseHexAddr(srcAddr) + byteOffset:X2}"
                            : srcAddr + "+" + byteOffset;
                        SelectBank(srcHi);
                        Emit("MOVF", srcHi, "W");
                    }
                    else
                    {
                        LoadIntoW(arg.Src1);
                    }

                    StoreWInto(arg.Dst);

                    string dstAddr = ResolveAddress(arg.Dst);
                    for (int i = 0; i < bitShift; i++)
                    {
                        Emit("BCF", "STATUS", "0");
                        SelectBank(dstAddr);
                        Emit(rotateOp, dstAddr, "F");
                    }
                }
                else
                {
                    // Variable shift
                    LoadIntoW(arg.Src1);
                    StoreWInto(arg.Dst);

                    string dstAddr = ResolveAddress(arg.Dst);
                    string countAddr = ResolveAddress(arg.Src2);
                    string loopLabel = MakeLabel("shift");
                    string doneLabel = MakeLabel("shift_done");

                    SelectBank(countAddr);
                    Emit("MOVF", countAddr, "W");
                    Emit("BTFSC", "STATUS", "2");
                    Emit("GOTO", doneLabel);
                    EmitLabel(loopLabel);
                    Emit("BCF", "STATUS", "0");
                    SelectBank(dstAddr);
                    Emit(rotateOp, dstAddr, "F");
                    SelectBank(countAddr);
                    Emit("DECFSZ", countAddr, "F");
                    Emit("GOTO", loopLabel);
                    EmitLabel(doneLabel);
                }

                return;
            }

            // Optimization: src2 is a constant
            if (arg.Src2 is Constant c2)
            {
                LoadIntoW(arg.Src1);
                int val = c2.Value & 0xFF;
                string valStr = $"0x{val:X2}";
                switch (arg.Op)
                {
                    case IrBinOp.Add: Emit("ADDLW", valStr); break;
                    case IrBinOp.BitAnd: Emit("ANDLW", valStr); break;
                    case IrBinOp.BitOr: Emit("IORLW", valStr); break;
                    case IrBinOp.BitXor: Emit("XORLW", valStr); break;
                    case IrBinOp.Sub:
                        int negVal = (-c2.Value) & 0xFF;
                        Emit("ADDLW", $"0x{negVal:X2}");
                        break;
                    default: break;
                }

                StoreWInto(arg.Dst);
                return;
            }

            LoadIntoW(arg.Src2);
            string a1 = ResolveAddress(arg.Src1);

            switch (arg.Op)
            {
                case IrBinOp.Add:
                    if (arg.Src1 is Constant) Emit("ADDLW", a1);
                    else
                    {
                        SelectBank(a1);
                        Emit("ADDWF", a1, "W");
                    }

                    break;
                case IrBinOp.Sub:
                    if (arg.Src1 is Constant c1s) Emit("SUBLW", $"0x{c1s.Value & 0xFF:X2}");
                    else
                    {
                        SelectBank(a1);
                        Emit("SUBWF", a1, "W");
                    }

                    break;
                case IrBinOp.BitAnd:
                    if (arg.Src1 is Constant) Emit("ANDLW", a1);
                    else
                    {
                        SelectBank(a1);
                        Emit("ANDWF", a1, "W");
                    }

                    break;
                case IrBinOp.BitOr:
                    if (arg.Src1 is Constant) Emit("IORLW", a1);
                    else
                    {
                        SelectBank(a1);
                        Emit("IORWF", a1, "W");
                    }

                    break;
                case IrBinOp.BitXor:
                    if (arg.Src1 is Constant) Emit("XORLW", a1);
                    else
                    {
                        SelectBank(a1);
                        Emit("XORWF", a1, "W");
                    }

                    break;
                default: break;
            }

            StoreWInto(arg.Dst);
            return;
        }

        // Generic fallback comparison
        LoadIntoW(arg.Src2);
        string addr1g = ResolveAddress(arg.Src1);
        SelectBank(addr1g);
        Emit("SUBWF", addr1g, "W");

        string dstAddrG = ResolveAddress(arg.Dst);
        SelectBank(dstAddrG);
        Emit("CLRF", dstAddrG);

        switch (arg.Op)
        {
            case IrBinOp.Equal: Emit("BTFSC", "STATUS", "2"); break;
            case IrBinOp.NotEqual: Emit("BTFSS", "STATUS", "2"); break;
            case IrBinOp.LessThan: Emit("BTFSS", "STATUS", "0"); break;
            case IrBinOp.GreaterEqual: Emit("BTFSC", "STATUS", "0"); break;
            case IrBinOp.GreaterThan:
            {
                string lblSkip = MakeLabel("L_GT");
                Emit("BTFSS", "STATUS", "0");
                Emit("GOTO", lblSkip);
                Emit("BTFSC", "STATUS", "2");
                Emit("GOTO", lblSkip);
                Emit("INCF", dstAddrG, "F");
                EmitLabel(lblSkip);
                return;
            }
            case IrBinOp.LessEqual:
            {
                string lblSet = MakeLabel("L_LE");
                string lblSkip = MakeLabel("L_LES");
                Emit("BTFSS", "STATUS", "0");
                Emit("GOTO", lblSet);
                Emit("BTFSS", "STATUS", "2");
                Emit("GOTO", lblSkip);
                EmitLabel(lblSet);
                Emit("INCF", dstAddrG, "F");
                EmitLabel(lblSkip);
                return;
            }
            default: break;
        }

        Emit("INCF", dstAddrG, "F");
    }

    private void CompileVariant(BitSet arg)
    {
        string addr = ResolveAddress(arg.Target);
        SelectBank(addr);
        Emit("BSF", addr, arg.Bit.ToString());
    }

    private void CompileVariant(BitClear arg)
    {
        string addr = ResolveAddress(arg.Target);
        SelectBank(addr);
        Emit("BCF", addr, arg.Bit.ToString());
    }

    private void CompileVariant(BitCheck arg)
    {
        string addr = ResolveAddress(arg.Source);
        SelectBank(addr);

        string dstAddr = ResolveAddress(arg.Dst);
        SelectBank(dstAddr);
        Emit("CLRF", dstAddr);
        Emit("BTFSC", addr, arg.Bit.ToString());
        SelectBank(dstAddr);
        Emit("INCF", dstAddr, "F");
    }

    private void CompileVariant(BitWrite arg)
    {
        if (arg.Src is Constant c)
        {
            string addr = ResolveAddress(arg.Target);
            SelectBank(addr);
            if (c.Value != 0)
                Emit("BSF", addr, arg.Bit.ToString());
            else
                Emit("BCF", addr, arg.Bit.ToString());
            return;
        }

        LoadIntoW(arg.Src);
        Emit("IORLW", "0");

        string addrBW = ResolveAddress(arg.Target);
        string bitStr = arg.Bit.ToString();
        string lblZero = MakeLabel("L_BZ");
        string lblEnd = MakeLabel("L_BE");

        Emit("BTFSC", "STATUS", "2");
        Emit("GOTO", lblZero);
        SelectBank(addrBW);
        Emit("BSF", addrBW, bitStr);
        Emit("GOTO", lblEnd);
        EmitLabel(lblZero);
        SelectBank(addrBW);
        Emit("BCF", addrBW, bitStr);
        EmitLabel(lblEnd);
    }

    private void CompileVariant(Label arg)
    {
        EmitLabel(arg.Name);
        // Label could be a jump target from anywhere — bank state is unknown.
        _currentBank = -1;
    }

    private void CompileVariant(Jump arg) => Emit("GOTO", arg.Target);

    private void CompileVariant(JumpIfZero arg)
    {
        if (arg.Condition is Constant c)
        {
            if (c.Value == 0) Emit("GOTO", arg.Target);
            return;
        }

        LoadIntoW(arg.Condition);
        Emit("IORLW", "0");
        Emit("BTFSC", "STATUS", "2");
        Emit("GOTO", arg.Target);
    }

    private void CompileVariant(JumpIfNotZero arg)
    {
        if (arg.Condition is Constant c)
        {
            if (c.Value != 0) Emit("GOTO", arg.Target);
            return;
        }

        LoadIntoW(arg.Condition);
        Emit("IORLW", "0");
        Emit("BTFSS", "STATUS", "2");
        Emit("GOTO", arg.Target);
    }

    private void CompileVariant(Call arg)
    {
        Emit("CALL", arg.FunctionName);
        // Called function may change BSR; invalidate both tracking
        _currentBank = -1;
        _strategy.InvalidateBank();
        if (arg.Dst is Variable || arg.Dst is Temporary)
            StoreWInto(arg.Dst);
    }

    private void CompileVariant(JumpIfBitSet arg)
    {
        string addr = ResolveAddress(arg.Source);
        SelectBank(addr);
        // BTFSC: skip next if bit is CLEAR (execute GOTO only when bit is SET)
        Emit("BTFSC", addr, arg.Bit.ToString());
        Emit("GOTO", arg.Target);
    }

    private void CompileVariant(JumpIfBitClear arg)
    {
        string addr = ResolveAddress(arg.Source);
        SelectBank(addr);
        // BTFSS: skip next if bit is SET (execute GOTO only when bit is CLEAR)
        Emit("BTFSS", addr, arg.Bit.ToString());
        Emit("GOTO", arg.Target);
    }

    private void CompileVariant(AugAssign arg)
    {
        string targetAddr = ResolveAddress(arg.Target);
        string targetAddrHi = targetAddr;

        int size = 1;
        string varName = "";
        if (arg.Target is Variable tv)
        {
            varName = tv.Name;
        }
        else if (arg.Target is Temporary tt)
        {
            varName = tt.Name;
        }

        if (!string.IsNullOrEmpty(varName) && _varSizes.ContainsKey(varName))
            size = _varSizes[varName];

        if (size > 1)
        {
            targetAddrHi = targetAddr.StartsWith("0x")
                ? $"0x{ParseHexAddr(targetAddr) + 1:X2}"
                : targetAddr + "+1";
        }

        // --- 8-bit ---
        if (size == 1)
        {
            SelectBank(targetAddr);
            LoadIntoW(arg.Operand);

            switch (arg.Op)
            {
                case IrBinOp.Add: Emit("ADDWF", targetAddr, "F"); break;
                case IrBinOp.Sub: Emit("SUBWF", targetAddr, "F"); break;
                case IrBinOp.BitAnd: Emit("ANDWF", targetAddr, "F"); break;
                case IrBinOp.BitOr: Emit("IORWF", targetAddr, "F"); break;
                case IrBinOp.BitXor: Emit("XORWF", targetAddr, "F"); break;
                case IrBinOp.LShift:
                {
                    string loopLbl = MakeLabel("augls");
                    string doneLbl = MakeLabel("augls_done");
                    Emit("MOVWF", "__tmp");
                    Emit("MOVF", "__tmp", "F");
                    Emit("BTFSC", "STATUS", "2");
                    Emit("GOTO", doneLbl);
                    EmitLabel(loopLbl);
                    Emit("BCF", "STATUS", "0");
                    Emit("RLF", targetAddr, "F");
                    Emit("DECFSZ", "__tmp", "F");
                    Emit("GOTO", loopLbl);
                    EmitLabel(doneLbl);
                    break;
                }
                case IrBinOp.RShift:
                {
                    string loopLbl = MakeLabel("augrs");
                    string doneLbl = MakeLabel("augrs_done");
                    Emit("MOVWF", "__tmp");
                    Emit("MOVF", "__tmp", "F");
                    Emit("BTFSC", "STATUS", "2");
                    Emit("GOTO", doneLbl);
                    EmitLabel(loopLbl);
                    Emit("BCF", "STATUS", "0");
                    Emit("RRF", targetAddr, "F");
                    Emit("DECFSZ", "__tmp", "F");
                    Emit("GOTO", loopLbl);
                    EmitLabel(doneLbl);
                    break;
                }
                default:
                    throw new NotSupportedException("PIC14: AugAssign 8-bit op not implemented");
            }

            return;
        }

        // --- 16-bit ---
        if (size == 2)
        {
            // 1. Bitwise Ops
            if (arg.Op == IrBinOp.BitAnd || arg.Op == IrBinOp.BitOr || arg.Op == IrBinOp.BitXor)
            {
                int valLo = 0, valHi = 0;
                bool isConst = false;
                if (arg.Operand is Constant c)
                {
                    valLo = c.Value & 0xFF;
                    valHi = (c.Value >> 8) & 0xFF;
                    isConst = true;
                }

                string opStr = arg.Op == IrBinOp.BitAnd ? "ANDWF"
                    : arg.Op == IrBinOp.BitOr ? "IORWF"
                    : "XORWF";
                string litOpStr = arg.Op == IrBinOp.BitAnd ? "ANDLW"
                    : arg.Op == IrBinOp.BitOr ? "IORLW"
                    : "XORLW";

                SelectBank(targetAddr);
                if (isConst)
                {
                    Emit("MOVF", targetAddr, "W");
                    Emit(litOpStr, $"0x{valLo:X2}");
                    Emit("MOVWF", targetAddr);
                }
                else
                {
                    LoadIntoW(arg.Operand);
                    Emit(opStr, targetAddr, "F");
                }

                // High byte
                SelectBank(targetAddrHi);
                if (isConst)
                {
                    Emit("MOVF", targetAddrHi, "W");
                    Emit(litOpStr, $"0x{valHi:X2}");
                    Emit("MOVWF", targetAddrHi);
                }
                else
                {
                    throw new NotSupportedException(
                        "PIC14: 16-bit AugAssign non-const bitwise not fully implemented");
                }

                return;
            }

            // 2. Add
            if (arg.Op == IrBinOp.Add)
            {
                if (arg.Operand is Constant addC)
                {
                    Emit("MOVLW", $"0x{addC.Value & 0xFF:X2}");
                    SelectBank(targetAddr);
                    Emit("ADDWF", targetAddr, "F");
                    Emit("BTFSC", "STATUS", "0");
                    Emit("INCF", targetAddrHi, "F");
                    Emit("MOVLW", $"0x{(addC.Value >> 8) & 0xFF:X2}");
                    SelectBank(targetAddrHi);
                    Emit("ADDWF", targetAddrHi, "F");
                }
                else
                {
                    // var16 += var16:
                    //   W = operand_lo; target_lo += W (sets Carry)
                    //   if carry: target_hi++
                    //   W = operand_hi; target_hi += W
                    string opAddr = ResolveAddress(arg.Operand);
                    string opAddrHi = opAddr.StartsWith("0x")
                        ? $"0x{ParseHexAddr(opAddr) + 1:X2}"
                        : opAddr + "+1";
                    SelectBank(opAddr);
                    Emit("MOVF", opAddr, "W");
                    SelectBank(targetAddr);
                    Emit("ADDWF", targetAddr, "F");
                    SelectBank(targetAddrHi);
                    Emit("BTFSC", "STATUS", "0");
                    Emit("INCF", targetAddrHi, "F");
                    SelectBank(opAddrHi);
                    Emit("MOVF", opAddrHi, "W");
                    SelectBank(targetAddrHi);
                    Emit("ADDWF", targetAddrHi, "F");
                }

                return;
            }

            // 3. Sub
            if (arg.Op == IrBinOp.Sub)
            {
                int valLo = 0, valHi = 0;
                bool isConst = false;
                if (arg.Operand is Constant c)
                {
                    valLo = c.Value & 0xFF;
                    valHi = (c.Value >> 8) & 0xFF;
                    isConst = true;
                }

                if (isConst)
                    Emit("MOVLW", $"0x{valLo:X2}");
                else
                    LoadIntoW(arg.Operand);

                SelectBank(targetAddr);
                Emit("SUBWF", targetAddr, "F");
                SelectBank(targetAddrHi);
                Emit("BTFSS", "STATUS", "0");
                Emit("DECF", targetAddrHi, "F");

                if (isConst)
                {
                    Emit("MOVLW", $"0x{valHi:X2}");
                    Emit("SUBWF", targetAddrHi, "F");
                }
                else
                {
                    if (arg.Operand is Variable opVar)
                    {
                        string opHi = opVar.Name + "+1";
                        SelectBank(opHi);
                        Emit("MOVF", opHi, "W");
                        SelectBank(targetAddrHi);
                        Emit("SUBWF", targetAddrHi, "F");
                    }
                }

                return;
            }

            // 4. LShift 16-bit
            if (arg.Op == IrBinOp.LShift)
            {
                string loopLbl = MakeLabel("augls16");
                string doneLbl = MakeLabel("augls16_done");
                LoadIntoW(arg.Operand);
                Emit("MOVWF", "__tmp");
                EmitLabel(loopLbl);
                Emit("MOVF", "__tmp", "F");
                Emit("BTFSC", "STATUS", "2");
                Emit("GOTO", doneLbl);
                Emit("BCF", "STATUS", "0");
                SelectBank(targetAddr);
                Emit("RLF", targetAddr, "F");
                SelectBank(targetAddrHi);
                Emit("RLF", targetAddrHi, "F");
                Emit("DECF", "__tmp", "F");
                Emit("GOTO", loopLbl);
                EmitLabel(doneLbl);
                return;
            }

            // 5. RShift 16-bit
            if (arg.Op == IrBinOp.RShift)
            {
                string loopLbl = MakeLabel("augrs16");
                string doneLbl = MakeLabel("augrs16_done");
                LoadIntoW(arg.Operand);
                Emit("MOVWF", "__tmp");
                EmitLabel(loopLbl);
                Emit("MOVF", "__tmp", "F");
                Emit("BTFSC", "STATUS", "2");
                Emit("GOTO", doneLbl);
                Emit("BCF", "STATUS", "0");
                SelectBank(targetAddrHi);
                Emit("RRF", targetAddrHi, "F");
                SelectBank(targetAddr);
                Emit("RRF", targetAddr, "F");
                Emit("DECF", "__tmp", "F");
                Emit("GOTO", loopLbl);
                EmitLabel(doneLbl);
                return;
            }
        }

        throw new NotSupportedException("PIC14: AugAssign size/op not fully implemented");
    }

    private void EmitFloatAdd(string target, string source)
    {
        // Move 4 bytes of source to __FP_B
        for (int i = 0; i < 4; i++)
        {
            string srcByte = i == 0
                ? source
                : source.StartsWith("0x")
                    ? $"0x{ParseHexAddr(source) + i:X2}"
                    : source + "+" + i;
            SelectBank(srcByte);
            Emit("MOVF", srcByte, "W");
            Emit("MOVWF", $"__FP_B+{i}");
        }

        // Move 4 bytes of target to __FP_A
        for (int i = 0; i < 4; i++)
        {
            string tgtByte = i == 0
                ? target
                : target.StartsWith("0x")
                    ? $"0x{ParseHexAddr(target) + i:X2}"
                    : target + "+" + i;
            SelectBank(tgtByte);
            Emit("MOVF", tgtByte, "W");
            Emit("MOVWF", $"__FP_A+{i}");
        }

        Emit("CALL", "__add_float");

        // Move 4 bytes of __FP_A back to target
        for (int i = 0; i < 4; i++)
        {
            string tgtByte = i == 0
                ? target
                : target.StartsWith("0x")
                    ? $"0x{ParseHexAddr(target) + i:X2}"
                    : target + "+" + i;
            Emit("MOVF", $"__FP_A+{i}", "W");
            SelectBank(tgtByte);
            Emit("MOVWF", tgtByte);
        }
    }

    private void CompileVariant(InlineAsm arg)
    {
        _assembly.Add(PIC14AsmLine.MakeRaw(arg.Code));
    }

    private void CompileVariant(DebugLine arg)
    {
        if (!string.IsNullOrEmpty(arg.SourceFile))
            EmitComment($"{arg.SourceFile}:{arg.Line}: {arg.Text}");
        else
            EmitComment($"Line {arg.Line}: {arg.Text}");
    }

    private void CompileVariant(JumpIfEqual arg)
    {
        // if (src1 == src2) goto target
        if (arg.Src2 is Constant c2 && c2.Value >= 0 && c2.Value <= 255)
        {
            string src1 = ResolveAddress(arg.Src1);
            SelectBank(src1);
            Emit("MOVF", src1, "W");
            Emit("XORLW", $"0x{c2.Value:X2}");
            Emit("BTFSC", "STATUS", "2");
            Emit("GOTO", arg.Target);
            return;
        }

        // Generic fallback
        LoadIntoW(arg.Src2);
        string s1 = ResolveAddress(arg.Src1);
        SelectBank(s1);
        Emit("SUBWF", s1, "W");
        Emit("BTFSC", "STATUS", "2");
        Emit("GOTO", arg.Target);
    }

    private void CompileVariant(JumpIfNotEqual arg)
    {
        if (arg.Src2 is Constant c2 && c2.Value >= 0 && c2.Value <= 255)
        {
            string src1 = ResolveAddress(arg.Src1);
            SelectBank(src1);
            Emit("MOVF", src1, "W");
            Emit("XORLW", $"0x{c2.Value:X2}");
            Emit("BTFSS", "STATUS", "2");
            Emit("GOTO", arg.Target);
            return;
        }

        LoadIntoW(arg.Src2);
        string s1 = ResolveAddress(arg.Src1);
        SelectBank(s1);
        Emit("SUBWF", s1, "W");
        Emit("BTFSS", "STATUS", "2");
        Emit("GOTO", arg.Target);
    }

    private void CompileVariant(JumpIfLessThan arg)
    {
        // if (src1 < src2) goto target
        if (arg.Src2 is Constant c2 && c2.Value >= 0 && c2.Value <= 255)
        {
            int k = c2.Value;
            if (k == 0) return; // Unsigned < 0 is never true
            string src1 = ResolveAddress(arg.Src1);
            SelectBank(src1);
            Emit("MOVF", src1, "W");
            Emit("SUBLW", $"0x{k - 1:X2}");
            Emit("BTFSC", "STATUS", "0");
            Emit("GOTO", arg.Target);
            return;
        }

        LoadIntoW(arg.Src2);
        string s1 = ResolveAddress(arg.Src1);
        SelectBank(s1);
        Emit("SUBWF", s1, "W");
        Emit("BTFSS", "STATUS", "0");
        Emit("GOTO", arg.Target);
    }

    private void CompileVariant(JumpIfLessOrEqual arg)
    {
        // if (src1 <= src2) goto target
        if (arg.Src2 is Constant c2 && c2.Value >= 0 && c2.Value <= 255)
        {
            int k = c2.Value;
            string src1 = ResolveAddress(arg.Src1);
            SelectBank(src1);
            Emit("MOVF", src1, "W");
            Emit("SUBLW", $"0x{k:X2}");
            Emit("BTFSC", "STATUS", "0");
            Emit("GOTO", arg.Target);
            return;
        }

        // Generic: reverse operands — src2 - src1 >= 0 -> src1 <= src2
        LoadIntoW(arg.Src1);
        string src2Addr = ResolveAddress(arg.Src2);
        SelectBank(src2Addr);
        Emit("SUBWF", src2Addr, "W");
        Emit("BTFSC", "STATUS", "0");
        Emit("GOTO", arg.Target);
    }

    private void CompileVariant(JumpIfGreaterThan arg)
    {
        // if (src1 > src2) goto target
        if (arg.Src2 is Constant c2 && c2.Value >= 0 && c2.Value <= 255)
        {
            int k = c2.Value;
            string src1 = ResolveAddress(arg.Src1);
            SelectBank(src1);
            Emit("MOVF", src1, "W");
            Emit("SUBLW", $"0x{k:X2}");
            Emit("BTFSS", "STATUS", "0");
            Emit("GOTO", arg.Target);
            return;
        }

        // Generic: flip operands — src2 - src1 < 0 -> C=0 -> src1 > src2
        LoadIntoW(arg.Src1);
        string src2Addr = ResolveAddress(arg.Src2);
        SelectBank(src2Addr);
        Emit("SUBWF", src2Addr, "W");
        Emit("BTFSS", "STATUS", "0");
        Emit("GOTO", arg.Target);
    }

    private void CompileVariant(JumpIfGreaterOrEqual arg)
    {
        // if (src1 >= src2) goto target
        if (arg.Src2 is Constant c2 && c2.Value >= 0 && c2.Value <= 255)
        {
            int k = c2.Value;
            if (k == 0)
            {
                Emit("GOTO", arg.Target);
                return;
            }

            string src1 = ResolveAddress(arg.Src1);
            SelectBank(src1);
            Emit("MOVF", src1, "W");
            Emit("SUBLW", $"0x{k - 1:X2}");
            Emit("BTFSS", "STATUS", "0");
            Emit("GOTO", arg.Target);
            return;
        }

        LoadIntoW(arg.Src2);
        string s1 = ResolveAddress(arg.Src1);
        SelectBank(s1);
        Emit("SUBWF", s1, "W");
        Emit("BTFSC", "STATUS", "0");
        Emit("GOTO", arg.Target);
    }
}