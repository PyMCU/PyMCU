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

namespace PyMCU.Backend.Targets.PIC18;

public class PIC18CodeGen : CodeGen
    {
        private readonly DeviceConfig config;
        private List<PIC18AsmLine> assembly = new();
        private Dictionary<string, int> symbolTable = new();
        private Dictionary<string, int> stackLayout = new();
        private int ramHead;
        private int labelCounter;
        private int currentBank = -1;

        public PIC18CodeGen(DeviceConfig cfg)
        {
            config = cfg;
            ramHead = 0x60; // Start after Access Bank
        }

        public override void EmitContextSave()
        {
        }

        public override void EmitContextRestore()
        {
        }

        public override void EmitInterruptReturn() => Emit("RETFIE", "FAST");

        private string MakeLabel(string prefix) => $"{prefix}_{labelCounter++}";

        private void Emit(string m) => assembly.Add(PIC18AsmLine.MakeInstruction(m));
        private void Emit(string m, string o1) => assembly.Add(PIC18AsmLine.MakeInstruction(m, o1));
        private void Emit(string m, string o1, string o2) => assembly.Add(PIC18AsmLine.MakeInstruction(m, o1, o2));

        private void Emit(string m, string o1, string o2, string o3) =>
            assembly.Add(PIC18AsmLine.MakeInstruction(m, o1, o2, o3));

        private void EmitLabel(string l)
        {
            assembly.Add(PIC18AsmLine.MakeLabel(l));
            currentBank = -1;
        }

        private void EmitComment(string c) => assembly.Add(PIC18AsmLine.MakeComment(c));
        private void EmitRaw(string t) => assembly.Add(PIC18AsmLine.MakeRaw(t));

        private string GetOrAllocVariable(string name)
        {
            if (!symbolTable.ContainsKey(name)) symbolTable[name] = ramHead++;
            return name;
        }

        private int GetAddress(string operand)
        {
            if (string.IsNullOrEmpty(operand)) return -1;
            if (operand.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    return Convert.ToInt32(operand, 16);
                }
                catch
                {
                    return -1;
                }
            }

            if (symbolTable.TryGetValue(operand, out int sa)) return sa;
            if (stackLayout.TryGetValue(operand, out int sl)) return 0x60 + sl;
            return -1;
        }

        private string GetAccessMode(string operand)
        {
            int addr = GetAddress(operand);
            if (addr == -1) return "ACCESS";
            if (addr <= 0x5F || addr >= 0xF60) return "ACCESS";
            return "BANKED";
        }

        private void SelectBank(string operand)
        {
            int addr = GetAddress(operand);
            if (addr == -1) return;
            if (addr <= 0x5F || addr >= 0xF60) return;
            int newBank = (addr >> 8) & 0x0F;
            if (currentBank == newBank) return;
            Emit("MOVLB", $"{newBank}");
            currentBank = newBank;
        }

        private string ResolveAddress(Val val)
        {
            if (val is NoneVal) return "";
            if (val is Constant c) return $"0x{c.Value & 0xFF:X2}";
            if (val is MemoryAddress mem) return $"0x{mem.Address:X2}";
            string name = val switch { Variable v => v.Name, Temporary t => t.Name, _ => "" };
            if (string.IsNullOrEmpty(name)) return "";
            if (stackLayout.ContainsKey(name)) return name;
            return GetOrAllocVariable(name);
        }

        private void LoadIntoW(Val val)
        {
            if (val is Constant c)
            {
                Emit("MOVLW", $"0x{c.Value & 0xFF:X2}");
            }
            else
            {
                string addr = ResolveAddress(val);
                SelectBank(addr);
                Emit("MOVF", addr, "W", GetAccessMode(addr));
            }
        }

        private void StoreWInto(Val val)
        {
            string addr = ResolveAddress(val);
            SelectBank(addr);
            Emit("MOVWF", addr, GetAccessMode(addr));
        }

        private void EmitConfigDirectives()
        {
            foreach (var (key, val) in config.Fuses)
                EmitRaw($"\tCONFIG {key} = {val}");
        }

        public override void Compile(ProgramIR program, TextWriter output)
        {
            assembly.Clear();
            currentBank = -1;

            var allocator = new StackAllocator();
            var (offsets, _) = allocator.Allocate(program);
            stackLayout = offsets;

            // 1. Compile functions first to populate symbol_table
            foreach (var func in program.Functions)
                CompileFunction(func);
            var functionCode = new List<PIC18AsmLine>(assembly);
            assembly.Clear();

            // 2. Emit Headers
            string chipFile = config.Chip.ToLowerInvariant();
            string listP = config.Chip;
            if (listP.Length > 3 && listP.StartsWith("pic", StringComparison.OrdinalIgnoreCase))
                listP = listP.Substring(3);
            listP = listP.ToUpperInvariant();
            EmitRaw($"\tLIST P={listP}");

            if (chipFile.StartsWith("pic"))
                chipFile = "p" + chipFile.Substring(3);
            EmitRaw($"\t#include <{chipFile}.inc>");
            EmitConfigDirectives();

            EmitRaw("_stack_base EQU 0x060");
            foreach (var (name, offset) in stackLayout)
                EmitRaw($"{name} EQU _stack_base + 0x{offset:X3}");

            // 3. Emit Global Variables
            foreach (var (name, addr) in symbolTable)
                EmitRaw($"{name} EQU 0x{addr:X3}");

            EmitRaw("    ORG 0x0000");
            Emit("GOTO", "main");

            // 4. Append function code
            assembly.AddRange(functionCode);

            // Run peephole
            var optimized = PIC18Peephole.Optimize(assembly);
            foreach (var line in optimized)
                output.WriteLine(line.ToString());

            output.WriteLine("\tEND");
        }

        private void CompileFunction(Function func)
        {
            EmitLabel(func.Name);
            foreach (var instr in func.Body)
                CompileInstruction(instr);
        }

        private void CompileInstruction(Instruction instr)
        {
            switch (instr)
            {
                case Return arg: CompileReturn(arg); break;
                case Copy arg: CompileCopy(arg); break;
                case Unary arg: CompileUnary(arg); break;
                case Binary arg: CompileBinary(arg); break;
                case Jump arg: Emit("BRA", arg.Target); break;
                case JumpIfZero arg: CompileJumpIfZero(arg); break;
                case JumpIfNotZero arg: CompileJumpIfNotZero(arg); break;
                case Label arg: EmitLabel(arg.Name); break;
                case Call arg: CompileCall(arg); break;
                case BitSet arg: CompileBitSet(arg); break;
                case BitClear arg: CompileBitClear(arg); break;
                case BitCheck arg: CompileBitCheck(arg); break;
                case BitWrite arg: CompileBitWrite(arg); break;
                case JumpIfBitSet arg: CompileJumpIfBitSet(arg); break;
                case JumpIfBitClear arg: CompileJumpIfBitClear(arg); break;
                case JumpIfEqual arg: CompileJumpIfEqual(arg); break;
                case JumpIfNotEqual arg: CompileJumpIfNotEqual(arg); break;
                case JumpIfLessThan arg: CompileJumpIfLessThan(arg); break;
                case JumpIfLessOrEqual arg: CompileJumpIfLessOrEqual(arg); break;
                case JumpIfGreaterThan arg: CompileJumpIfGreaterThan(arg); break;
                case JumpIfGreaterOrEqual arg: CompileJumpIfGreaterOrEqual(arg); break;
                case AugAssign arg: CompileAugAssign(arg); break;
                case LoadIndirect arg: CompileLoadIndirect(arg); break;
                case StoreIndirect arg: CompileStoreIndirect(arg); break;
                case InlineAsm arg: assembly.Add(PIC18AsmLine.MakeRaw(arg.Code)); break;
                case DebugLine arg:
                    if (!string.IsNullOrEmpty(arg.SourceFile)) EmitComment($"{arg.SourceFile}:{arg.Line}: {arg.Text}");
                    else EmitComment($"Line {arg.Line}: {arg.Text}");
                    break;
                case ArrayLoad: break;
                case ArrayStore: break;
            }
        }

        private void CompileReturn(Return arg)
        {
            if (arg.Value is not NoneVal) LoadIntoW(arg.Value);
            Emit("RETURN");
        }

        private void CompileCopy(Copy arg)
        {
            if (arg.Src is Constant)
            {
                LoadIntoW(arg.Src);
                StoreWInto(arg.Dst);
            }
            else
            {
                string src = ResolveAddress(arg.Src);
                string dst = ResolveAddress(arg.Dst);
                Emit("MOVFF", src, dst);
            }
        }

        private void CompileUnary(Unary arg)
        {
            LoadIntoW(arg.Src);
            switch (arg.Op)
            {
                case PyMCU.IR.UnaryOp.Neg: Emit("NEGF", "WREG", "ACCESS"); break;
                case PyMCU.IR.UnaryOp.Not: Emit("COMF", "WREG", "W", "ACCESS"); break;
                case PyMCU.IR.UnaryOp.BitNot: Emit("COMF", "WREG", "W", "ACCESS"); break;
            }

            StoreWInto(arg.Dst);
        }

        private void CompileBinary(Binary arg)
        {
            if (arg.Op == PyMCU.IR.BinaryOp.Mul)
            {
                if (arg.Src2 is Constant c2m)
                {
                    LoadIntoW(arg.Src1);
                    Emit("MOVLW", $"0x{c2m.Value & 0xFF:X2}");
                    Emit("MULWF", "WREG", "ACCESS");
                    Emit("MOVF", "PRODL", "W", "ACCESS");
                    StoreWInto(arg.Dst);
                    return;
                }
                else
                {
                    LoadIntoW(arg.Src1);
                    string right = ResolveAddress(arg.Src2);
                    SelectBank(right);
                    Emit("MULWF", right, GetAccessMode(right));
                    Emit("MOVF", "PRODL", "W", "ACCESS");
                    StoreWInto(arg.Dst);
                    return;
                }
            }

            LoadIntoW(arg.Src1);
            string r = ResolveAddress(arg.Src2);

            switch (arg.Op)
            {
                case PyMCU.IR.BinaryOp.Add:
                    if (arg.Src2 is Constant) Emit("ADDLW", r);
                    else
                    {
                        SelectBank(r);
                        Emit("ADDWF", r, "W", GetAccessMode(r));
                    }

                    StoreWInto(arg.Dst);
                    break;

                case PyMCU.IR.BinaryOp.Sub:
                    if (arg.Src2 is Constant c2s)
                        Emit("ADDLW", $"(0x100 - ({r})) & 0xFF");
                    else if (arg.Src1 is Constant c1s)
                    {
                        LoadIntoW(arg.Src2);
                        Emit("SUBLW", $"0x{c1s.Value & 0xFF:X2}");
                    }
                    else
                    {
                        LoadIntoW(arg.Src2);
                        string left = ResolveAddress(arg.Src1);
                        SelectBank(left);
                        Emit("SUBWF", left, "W", GetAccessMode(left));
                    }

                    StoreWInto(arg.Dst);
                    break;

                case PyMCU.IR.BinaryOp.BitAnd:
                    if (arg.Src2 is Constant) Emit("ANDLW", r);
                    else
                    {
                        SelectBank(r);
                        Emit("ANDWF", r, "W", GetAccessMode(r));
                    }

                    StoreWInto(arg.Dst);
                    break;

                case PyMCU.IR.BinaryOp.BitOr:
                    if (arg.Src2 is Constant) Emit("IORLW", r);
                    else
                    {
                        SelectBank(r);
                        Emit("IORWF", r, "W", GetAccessMode(r));
                    }

                    StoreWInto(arg.Dst);
                    break;

                case PyMCU.IR.BinaryOp.BitXor:
                    if (arg.Src2 is Constant) Emit("XORLW", r);
                    else
                    {
                        SelectBank(r);
                        Emit("XORWF", r, "W", GetAccessMode(r));
                    }

                    StoreWInto(arg.Dst);
                    break;

                case PyMCU.IR.BinaryOp.LShift:
                    if (arg.Src2 is Constant c2ls)
                    {
                        int amount = c2ls.Value & 0x07;
                        for (int i = 0; i < amount; ++i)
                        {
                            Emit("BCF", "STATUS", "C", "ACCESS");
                            Emit("RLCF", "WREG", "F", "ACCESS");
                        }
                    }
                    else
                    {
                        string lblLoop = MakeLabel("shift_loop");
                        string lblEnd = MakeLabel("shift_end");
                        LoadIntoW(arg.Src2);
                        Emit("BZ", lblEnd);
                        string count = GetOrAllocVariable(MakeLabel("shift_count"));
                        Emit("MOVWF", count, "ACCESS");
                        LoadIntoW(arg.Src1);
                        EmitLabel(lblLoop);
                        Emit("BCF", "STATUS", "C", "ACCESS");
                        Emit("RLCF", "WREG", "F", "ACCESS");
                        Emit("DECFSZ", count, "F", "ACCESS");
                        Emit("BRA", lblLoop);
                        EmitLabel(lblEnd);
                    }

                    StoreWInto(arg.Dst);
                    break;

                case PyMCU.IR.BinaryOp.RShift:
                    if (arg.Src2 is Constant c2rs)
                    {
                        int amount = c2rs.Value & 0x07;
                        for (int i = 0; i < amount; ++i)
                        {
                            Emit("BCF", "STATUS", "C", "ACCESS");
                            Emit("RRCF", "WREG", "F", "ACCESS");
                        }
                    }
                    else
                    {
                        string lblLoop = MakeLabel("shift_loop");
                        string lblEnd = MakeLabel("shift_end");
                        LoadIntoW(arg.Src2);
                        Emit("BZ", lblEnd);
                        string count = GetOrAllocVariable(MakeLabel("shift_count"));
                        Emit("MOVWF", count, "ACCESS");
                        LoadIntoW(arg.Src1);
                        EmitLabel(lblLoop);
                        Emit("BCF", "STATUS", "C", "ACCESS");
                        Emit("RRCF", "WREG", "F", "ACCESS");
                        Emit("DECFSZ", count, "F", "ACCESS");
                        Emit("BRA", lblLoop);
                        EmitLabel(lblEnd);
                    }

                    StoreWInto(arg.Dst);
                    break;

                case PyMCU.IR.BinaryOp.Div:
                case PyMCU.IR.BinaryOp.FloorDiv:
                case PyMCU.IR.BinaryOp.Mod:
                {
                    string quot = GetOrAllocVariable(MakeLabel("div_quot"));
                    string rem = GetOrAllocVariable(MakeLabel("div_rem"));
                    string divisor = GetOrAllocVariable(MakeLabel("div_divisor"));
                    string lblLoop = MakeLabel("div_loop");
                    string lblEnd = MakeLabel("div_end");

                    LoadIntoW(arg.Src2);
                    Emit("MOVWF", divisor, "ACCESS");
                    LoadIntoW(arg.Src1);
                    Emit("MOVWF", rem, "ACCESS");
                    Emit("CLRF", quot, "ACCESS");

                    EmitLabel(lblLoop);
                    Emit("MOVF", divisor, "W", "ACCESS");
                    Emit("SUBWF", rem, "W", "ACCESS");
                    Emit("BN", lblEnd);
                    Emit("MOVWF", rem, "ACCESS");
                    Emit("INCF", quot, "F", "ACCESS");
                    Emit("BRA", lblLoop);
                    EmitLabel(lblEnd);

                    if (arg.Op == PyMCU.IR.BinaryOp.Mod) Emit("MOVF", rem, "W", "ACCESS");
                    else Emit("MOVF", quot, "W", "ACCESS");
                    StoreWInto(arg.Dst);
                    break;
                }

                case PyMCU.IR.BinaryOp.Equal:
                case PyMCU.IR.BinaryOp.NotEqual:
                case PyMCU.IR.BinaryOp.LessThan:
                case PyMCU.IR.BinaryOp.LessEqual:
                case PyMCU.IR.BinaryOp.GreaterThan:
                case PyMCU.IR.BinaryOp.GreaterEqual:
                {
                    string left = ResolveAddress(arg.Src1);
                    bool optimizeZero = arg.Src2 is Constant cz && cz.Value == 0;

                    if (optimizeZero)
                    {
                        SelectBank(left);
                        Emit("MOVF", left, "F", GetAccessMode(left));
                    }
                    else
                    {
                        LoadIntoW(arg.Src2);
                        SelectBank(left);
                        Emit("SUBWF", left, "W", GetAccessMode(left));
                    }

                    Emit("CLRF", "WREG", "ACCESS");
                    string lblFalse = MakeLabel("comp_false");
                    string lblTrue = MakeLabel("comp_true");

                    switch (arg.Op)
                    {
                        case PyMCU.IR.BinaryOp.Equal:
                            Emit("BTFSC", "STATUS", "Z", "ACCESS");
                            Emit("MOVLW", "1");
                            break;
                        case PyMCU.IR.BinaryOp.NotEqual:
                            Emit("BTFSS", "STATUS", "Z", "ACCESS");
                            Emit("MOVLW", "1");
                            break;
                        case PyMCU.IR.BinaryOp.LessThan:
                            Emit("BTFSS", "STATUS", "C", "ACCESS");
                            Emit("MOVLW", "1");
                            break;
                        case PyMCU.IR.BinaryOp.GreaterEqual:
                            Emit("BTFSC", "STATUS", "C", "ACCESS");
                            Emit("MOVLW", "1");
                            break;
                        case PyMCU.IR.BinaryOp.GreaterThan:
                            Emit("BTFSS", "STATUS", "C", "ACCESS");
                            Emit("BRA", lblFalse);
                            Emit("BTFSS", "STATUS", "Z", "ACCESS");
                            Emit("MOVLW", "1");
                            break;
                        case PyMCU.IR.BinaryOp.LessEqual:
                            Emit("BTFSC", "STATUS", "Z", "ACCESS");
                            Emit("BRA", lblTrue);
                            Emit("BTFSC", "STATUS", "C", "ACCESS");
                            Emit("BRA", lblFalse);
                            EmitLabel(lblTrue);
                            Emit("MOVLW", "1");
                            break;
                    }

                    EmitLabel(lblFalse);
                    StoreWInto(arg.Dst);
                    break;
                }
            }
        }

        private void CompileJumpIfZero(JumpIfZero arg)
        {
            LoadIntoW(arg.Condition);
            Emit("ANDLW", "0xFF");
            Emit("BZ", arg.Target);
        }

        private void CompileJumpIfNotZero(JumpIfNotZero arg)
        {
            LoadIntoW(arg.Condition);
            Emit("ANDLW", "0xFF");
            Emit("BNZ", arg.Target);
        }

        private void CompileCall(Call arg)
        {
            Emit("CALL", arg.FunctionName);
            if (arg.Dst is not NoneVal) StoreWInto(arg.Dst);
        }

        private void CompileBitSet(BitSet arg)
        {
            string addr = ResolveAddress(arg.Target);
            SelectBank(addr);
            Emit("BSF", addr, $"{arg.Bit}", GetAccessMode(addr));
        }

        private void CompileBitClear(BitClear arg)
        {
            string addr = ResolveAddress(arg.Target);
            SelectBank(addr);
            Emit("BCF", addr, $"{arg.Bit}", GetAccessMode(addr));
        }

        private void CompileBitCheck(BitCheck arg)
        {
            string addr = ResolveAddress(arg.Source);
            SelectBank(addr);
            Emit("CLRF", "WREG", "ACCESS");
            Emit("BTFSC", addr, $"{arg.Bit}", GetAccessMode(addr));
            Emit("MOVLW", "1");
            StoreWInto(arg.Dst);
        }

        private void CompileBitWrite(BitWrite arg)
        {
            string addr = ResolveAddress(arg.Target);
            SelectBank(addr);
            LoadIntoW(arg.Src);
            Emit("TSTFSZ", "WREG", "ACCESS");
            Emit("BRA", MakeLabel("set_bit"));
            Emit("BCF", addr, $"{arg.Bit}", GetAccessMode(addr));
            Emit("BRA", MakeLabel("end_bit_write"));
            EmitLabel(MakeLabel("set_bit"));
            Emit("BSF", addr, $"{arg.Bit}", GetAccessMode(addr));
            EmitLabel(MakeLabel("end_bit_write"));
        }

        private void CompileJumpIfBitSet(JumpIfBitSet arg)
        {
            string addr = ResolveAddress(arg.Source);
            SelectBank(addr);
            Emit("BTFSC", addr, $"{arg.Bit}", GetAccessMode(addr));
            Emit("BRA", arg.Target);
        }

        private void CompileJumpIfBitClear(JumpIfBitClear arg)
        {
            string addr = ResolveAddress(arg.Source);
            SelectBank(addr);
            Emit("BTFSS", addr, $"{arg.Bit}", GetAccessMode(addr));
            Emit("BRA", arg.Target);
        }

        private void CompileJumpIfEqual(JumpIfEqual arg)
        {
            if (arg.Src2 is Constant c2 && c2.Value >= 0 && c2.Value <= 255)
            {
                string src1 = ResolveAddress(arg.Src1);
                SelectBank(src1);
                Emit("MOVF", src1, "W", GetAccessMode(src1));
                Emit("XORLW", $"0x{c2.Value:X2}");
                Emit("BTFSC", "STATUS", "Z", "ACCESS");
                Emit("BRA", arg.Target);
                return;
            }

            LoadIntoW(arg.Src2);
            string s1 = ResolveAddress(arg.Src1);
            SelectBank(s1);
            Emit("SUBWF", s1, "W", GetAccessMode(s1));
            Emit("BTFSC", "STATUS", "Z", "ACCESS");
            Emit("BRA", arg.Target);
        }

        private void CompileJumpIfNotEqual(JumpIfNotEqual arg)
        {
            if (arg.Src2 is Constant c2 && c2.Value >= 0 && c2.Value <= 255)
            {
                string src1 = ResolveAddress(arg.Src1);
                SelectBank(src1);
                Emit("MOVF", src1, "W", GetAccessMode(src1));
                Emit("XORLW", $"0x{c2.Value:X2}");
                Emit("BTFSS", "STATUS", "Z", "ACCESS");
                Emit("BRA", arg.Target);
                return;
            }

            LoadIntoW(arg.Src2);
            string s1 = ResolveAddress(arg.Src1);
            SelectBank(s1);
            Emit("SUBWF", s1, "W", GetAccessMode(s1));
            Emit("BTFSS", "STATUS", "Z", "ACCESS");
            Emit("BRA", arg.Target);
        }

        private void CompileJumpIfLessThan(JumpIfLessThan arg)
        {
            if (arg.Src2 is Constant c2 && c2.Value >= 0 && c2.Value <= 255)
            {
                int k = c2.Value;
                if (k == 0) return;
                string src1 = ResolveAddress(arg.Src1);
                SelectBank(src1);
                Emit("MOVF", src1, "W", GetAccessMode(src1));
                Emit("SUBLW", $"0x{k - 1:X2}");
                Emit("BTFSC", "STATUS", "C", "ACCESS");
                Emit("BRA", arg.Target);
                return;
            }

            LoadIntoW(arg.Src2);
            string s1 = ResolveAddress(arg.Src1);
            SelectBank(s1);
            Emit("SUBWF", s1, "W", GetAccessMode(s1));
            Emit("BTFSS", "STATUS", "C", "ACCESS");
            Emit("BRA", arg.Target);
        }

        private void CompileJumpIfLessOrEqual(JumpIfLessOrEqual arg)
        {
            if (arg.Src2 is Constant c2 && c2.Value >= 0 && c2.Value <= 255)
            {
                string src1 = ResolveAddress(arg.Src1);
                SelectBank(src1);
                Emit("MOVF", src1, "W", GetAccessMode(src1));
                Emit("SUBLW", $"0x{c2.Value:X2}");
                Emit("BTFSC", "STATUS", "C", "ACCESS");
                Emit("BRA", arg.Target);
                return;
            }

            LoadIntoW(arg.Src1);
            string src2 = ResolveAddress(arg.Src2);
            SelectBank(src2);
            Emit("SUBWF", src2, "W", GetAccessMode(src2));
            Emit("BTFSC", "STATUS", "C", "ACCESS");
            Emit("BRA", arg.Target);
        }

        private void CompileJumpIfGreaterThan(JumpIfGreaterThan arg)
        {
            if (arg.Src2 is Constant c2 && c2.Value >= 0 && c2.Value <= 255)
            {
                string src1 = ResolveAddress(arg.Src1);
                SelectBank(src1);
                Emit("MOVF", src1, "W", GetAccessMode(src1));
                Emit("SUBLW", $"0x{c2.Value:X2}");
                Emit("BTFSS", "STATUS", "C", "ACCESS");
                Emit("BRA", arg.Target);
                return;
            }

            LoadIntoW(arg.Src1);
            string src2 = ResolveAddress(arg.Src2);
            SelectBank(src2);
            Emit("SUBWF", src2, "W", GetAccessMode(src2));
            Emit("BTFSS", "STATUS", "C", "ACCESS");
            Emit("BRA", arg.Target);
        }

        private void CompileJumpIfGreaterOrEqual(JumpIfGreaterOrEqual arg)
        {
            if (arg.Src2 is Constant c2 && c2.Value >= 0 && c2.Value <= 255)
            {
                int k = c2.Value;
                if (k == 0)
                {
                    Emit("BRA", arg.Target);
                    return;
                }

                string src1 = ResolveAddress(arg.Src1);
                SelectBank(src1);
                Emit("MOVF", src1, "W", GetAccessMode(src1));
                Emit("SUBLW", $"0x{k - 1:X2}");
                Emit("BTFSS", "STATUS", "C", "ACCESS");
                Emit("BRA", arg.Target);
                return;
            }

            LoadIntoW(arg.Src2);
            string s1 = ResolveAddress(arg.Src1);
            SelectBank(s1);
            Emit("SUBWF", s1, "W", GetAccessMode(s1));
            Emit("BTFSC", "STATUS", "C", "ACCESS");
            Emit("BRA", arg.Target);
        }

        private void CompileAugAssign(AugAssign aa)
        {
            string target = ResolveAddress(aa.Target);
            switch (aa.Op)
            {
                case PyMCU.IR.BinaryOp.Add:
                    LoadIntoW(aa.Operand);
                    SelectBank(target);
                    Emit("ADDWF", target, "F", GetAccessMode(target));
                    break;
                case PyMCU.IR.BinaryOp.Sub:
                    LoadIntoW(aa.Operand);
                    SelectBank(target);
                    Emit("SUBWF", target, "F", GetAccessMode(target));
                    break;
                case PyMCU.IR.BinaryOp.BitAnd:
                    LoadIntoW(aa.Operand);
                    SelectBank(target);
                    Emit("ANDWF", target, "F", GetAccessMode(target));
                    break;
                case PyMCU.IR.BinaryOp.BitOr:
                    LoadIntoW(aa.Operand);
                    SelectBank(target);
                    Emit("IORWF", target, "F", GetAccessMode(target));
                    break;
                case PyMCU.IR.BinaryOp.BitXor:
                    LoadIntoW(aa.Operand);
                    SelectBank(target);
                    Emit("XORWF", target, "F", GetAccessMode(target));
                    break;
                case PyMCU.IR.BinaryOp.LShift:
                    CompileAugAssignShift(target, aa.Operand, left: true);
                    break;
                case PyMCU.IR.BinaryOp.RShift:
                    CompileAugAssignShift(target, aa.Operand, left: false);
                    break;
                default:
                    throw new NotSupportedException($"PIC18: AugAssign op {aa.Op} not implemented");
            }
        }

        private void CompileAugAssignShift(string target, Val operand, bool left)
        {
            string shiftInstr = left ? "RLCF" : "RRCF";
            string targetMode = GetAccessMode(target);
            if (operand is Constant c)
            {
                int n = c.Value & 7;
                for (int i = 0; i < n; i++)
                {
                    Emit("BCF", "STATUS", "C", "ACCESS");
                    SelectBank(target);
                    Emit(shiftInstr, target, "F", targetMode);
                }
            }
            else
            {
                string count = GetOrAllocVariable(MakeLabel("aa_cnt"));
                LoadIntoW(operand);
                SelectBank(count);
                Emit("MOVWF", count, GetAccessMode(count));
                string loopL = MakeLabel("aa_sh_lp");
                string bodyL = MakeLabel("aa_sh_bd");
                string doneL = MakeLabel("aa_sh_dn");
                EmitLabel(loopL);
                SelectBank(count);
                Emit("TSTFSZ", count, GetAccessMode(count));
                Emit("BRA", bodyL);
                Emit("BRA", doneL);
                EmitLabel(bodyL);
                Emit("BCF", "STATUS", "C", "ACCESS");
                SelectBank(target);
                Emit(shiftInstr, target, "F", targetMode);
                SelectBank(count);
                Emit("DECF", count, "F", GetAccessMode(count));
                Emit("BRA", loopL);
                EmitLabel(doneL);
            }
        }

        private void CompileLoadIndirect(LoadIndirect li)
        {
            LoadIntoW(li.SrcPtr);
            Emit("MOVWF", "0xFE9", "ACCESS");      // FSR0L = pointer
            Emit("CLRF", "0xFEA", "ACCESS");       // FSR0H = 0
            Emit("MOVF", "0xFEF", "W", "ACCESS"); // W = [INDF0]
            StoreWInto(li.Dst);
        }

        private void CompileStoreIndirect(StoreIndirect si)
        {
            LoadIntoW(si.DstPtr);
            Emit("MOVWF", "0xFE9", "ACCESS");  // FSR0L = pointer
            Emit("CLRF", "0xFEA", "ACCESS");   // FSR0H = 0
            LoadIntoW(si.Src);
            Emit("MOVWF", "0xFEF", "ACCESS");  // [INDF0] = W
        }
}