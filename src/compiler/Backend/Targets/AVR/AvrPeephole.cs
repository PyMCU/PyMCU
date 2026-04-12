namespace PyMCU.Backend.Targets.AVR;

public class AvrAsmLine
{
    public enum LineType
    {
        Instruction,
        Label,
        Comment,
        Raw,
        Empty
    }

    public LineType Type;
    public string LabelText = "";
    public string Mnemonic = "";
    public string Op1 = "";
    public string Op2 = "";
    public string Content = "";

    public static AvrAsmLine MakeInstruction(string m, string o1 = "", string o2 = "")
        => new() { Type = LineType.Instruction, Mnemonic = m, Op1 = o1, Op2 = o2 };

    public static AvrAsmLine MakeLabel(string l)
        => new() { Type = LineType.Label, LabelText = l };

    public static AvrAsmLine MakeComment(string c)
        => new() { Type = LineType.Comment, Content = c };

    public static AvrAsmLine MakeRaw(string r)
        => new() { Type = LineType.Raw, Content = r };

    public static AvrAsmLine MakeEmpty()
        => new() { Type = LineType.Empty };

    public override string ToString()
    {
        switch (Type)
        {
            case LineType.Instruction:
                if (string.IsNullOrEmpty(Op1)) return $"\t{Mnemonic}";
                if (string.IsNullOrEmpty(Op2)) return $"\t{Mnemonic}\t{Op1}";
                return $"\t{Mnemonic}\t{Op1}, {Op2}";
            case LineType.Label:
                return $"{LabelText}:";
            case LineType.Comment:
                return $"; {Content}";
            case LineType.Raw:
                return Content;
            default:
                return "";
        }
    }
}

public static class AvrPeephole
{
    private static bool IsFlowTerminator(AvrAsmLine line)
    {
        return line.Type == AvrAsmLine.LineType.Instruction &&
               (line.Mnemonic == "RJMP" || line.Mnemonic == "JMP" ||
                line.Mnemonic == "RETI" || line.Mnemonic == "RET" ||
                line.Mnemonic.StartsWith("BR"));
    }

    private static int ParseYOffset(string s)
    {
        if (s.Length < 3 || s[0] != 'Y' || s[1] != '+') return -1;
        return int.TryParse(s.AsSpan(2), out int v) ? v : -1;
    }

    private static int ParseReg(string s)
    {
        if (s.Length < 2 || s[0] != 'R') return -1;
        return int.TryParse(s.AsSpan(1), out int v) ? v : -1;
    }

    public static List<AvrAsmLine> Optimize(List<AvrAsmLine> lines)
    {
        var result = new List<AvrAsmLine>(lines);
        var changed = true;

        while (changed)
        {
            changed = false;
            var next = new List<AvrAsmLine>();

            // --- Dead Label Elimination ---
            var usedLabels = new HashSet<string>();
            foreach (var line in result.Where(line => line.Type == AvrAsmLine.LineType.Instruction))
            {
                if (line.Mnemonic is "RJMP" or "RCALL" or "JMP" or "CALL" or
                    "BREQ" or "BRNE" or "BRLO" or "BRSH" or "BRMI" or "BRPL" or
                    "BRLT" or "BRGE" or "BRCS" or "BRCC")
                {
                    usedLabels.Add(line.Op1);
                }
            }

            usedLabels.Add("main");
            usedLabels.Add("__vector_default");

            int modCtr = 0;
            var aliases = new string[32];
            for (int k = 0; k < 32; ++k)
                aliases[k] = $"init_{modCtr++}";

            for (int i = 0; i < result.Count; ++i)
            {
                var current = result[i];

                if (current.Type == AvrAsmLine.LineType.Label)
                {
                    if (!usedLabels.Contains(current.LabelText) &&
                        (current.LabelText.StartsWith("L.") || current.LabelText.StartsWith("L_")))
                    {
                        changed = true;
                        continue;
                    }

                    for (int k = 0; k < 32; ++k)
                        aliases[k] = $"lab_{modCtr++}";
                    
                    next.Add(current);
                    continue;
                }

                if (current.Type != AvrAsmLine.LineType.Instruction)
                {
                    next.Add(current);
                    continue;
                }

                switch (current.Mnemonic)
                {
                    case "LDI":
                    {
                        var regIdx = ParseReg(current.Op1);
                        if (regIdx is >= 0 and < 32)
                        {
                            var valId = "ldi_" + current.Op2;
                            if (aliases[regIdx] == valId)
                            {
                                changed = true;
                                continue; // Redundant LDI
                            }
                            aliases[regIdx] = valId;
                        }
                        break;
                    }
                    case "MOV":
                    {
                        var dstIdx = ParseReg(current.Op1);
                        var srcIdx = ParseReg(current.Op2);
                        switch (dstIdx)
                        {
                            case >= 0 and < 32 when srcIdx is >= 0 and < 32:
                            {
                                if (aliases[dstIdx] == aliases[srcIdx])
                                {
                                    changed = true;
                                    continue; // Redundant MOV
                                }
                                aliases[dstIdx] = aliases[srcIdx];
                                break;
                            }
                            case >= 0 and < 32:
                                aliases[dstIdx] = $"mov_{modCtr++}";
                                break;
                        }
                        break;
                    }
                    case "STS" or "OUT" or "CP" or "TST" or "STD" or "SBI" or "CBI" or "SBIS" or "SBIC":
                        break;
                    case "LDS" or "IN" or "LDD":
                    {
                        var regIdx = ParseReg(current.Op1);
                        if (regIdx is >= 0 and < 32)
                            aliases[regIdx] = $"load_{modCtr++}";
                        break;
                    }
                    case "CLR":
                    {
                        var regIdx = ParseReg(current.Op1);
                        if (regIdx is >= 0 and < 32)
                            aliases[regIdx] = "ldi_0";
                        break;
                    }
                    case "ADD" or "SUB" or "INC" or "DEC" or "NEG" or "COM" or
                         "ORI" or "ANDI" or "EOR" or "AND" or "OR" or "ADC" or "SBC" or "LSR" or
                         "ASR" or "ROR" or "LSL" or "ROL" or "MUL" or "MULS" or "CPC" or "CPI":
                    {
                        int regIdx = ParseReg(current.Op1);
                        if (regIdx is >= 0 and < 32)
                            aliases[regIdx] = $"mod_{modCtr++}";
                        break;
                    }
                    default:
                    {
                        if (IsFlowTerminator(current))
                        {
                            if (current.Mnemonic == "RJMP")
                            {
                                var redundant = false;
                                for (var j = i + 1; j < result.Count; ++j)
                                {
                                    if (result[j].Type == AvrAsmLine.LineType.Label)
                                    {
                                        if (result[j].LabelText == current.Op1) redundant = true;
                                        break;
                                    }
                                    if (result[j].Type is AvrAsmLine.LineType.Comment or AvrAsmLine.LineType.Empty)
                                        continue;
                                    break;
                                }

                                if (redundant)
                                {
                                    changed = true;
                                    continue;
                                }
                            }

                            next.Add(current);
                            for (int k = 0; k < 32; ++k)
                                aliases[k] = $"flow_{modCtr++}";
                            continue;
                        }
                        else
                        {
                            for (int k = 0; k < 32; ++k)
                                aliases[k] = $"unk_{modCtr++}";
                        }
                        break;
                    }
                }

                next.Add(current);
            }
            result = next;
        }

        bool fwdChanged = true;
        while (fwdChanged)
        {
            fwdChanged = false;
            for (int i = 0; i + 1 < result.Count; ++i)
            {
                if (result[i].Type != AvrAsmLine.LineType.Instruction) continue;

                int j = i + 1;
                while (j < result.Count && result[j].Type is AvrAsmLine.LineType.Comment or AvrAsmLine.LineType.Empty) j++;
                if (j >= result.Count || result[j].Type != AvrAsmLine.LineType.Instruction) continue;

                var a = result[i];
                var b = result[j];

                // Pattern A: STD Y+N, Rx ; LDD Ry, Y+N
                if (a.Mnemonic == "STD" && b.Mnemonic == "LDD")
                {
                    int aOff = ParseYOffset(a.Op1);
                    int bOff = ParseYOffset(b.Op2);
                    if (aOff >= 0 && aOff == bOff)
                    {
                        int rx = ParseReg(a.Op2);
                        int ry = ParseReg(b.Op1);
                        if (rx >= 0 && ry >= 0)
                        {
                            if (rx == ry)
                            {
                                result[j] = AvrAsmLine.MakeEmpty();
                            }
                            else
                            {
                                result[j] = AvrAsmLine.MakeInstruction("MOV", $"R{ry}", $"R{rx}");
                            }
                            fwdChanged = true;
                        }
                    }
                }

                // Pattern B: LDD Rx, Y+N ; STD Y+N, Rx
                if (a.Mnemonic == "LDD" && b.Mnemonic == "STD")
                {
                    var aOff = ParseYOffset(a.Op2);
                    var bOff = ParseYOffset(b.Op1);
                    if (aOff >= 0 && aOff == bOff && a.Op1 == b.Op2)
                    {
                        result[j] = AvrAsmLine.MakeEmpty();
                        fwdChanged = true;
                    }
                }
            }
        }
        // MOV Ra, Rb; OP Ra; MOV Rb, Ra -> OP Rb; MOV Ra, Rb
        var win3 = true;
        while (win3)
        {
            win3 = false;
            for (var i = 0; i < result.Count; ++i)
            {
                if (result[i].Type != AvrAsmLine.LineType.Instruction) continue;
                
                var j2 = i + 1;
                while (j2 < result.Count && result[j2].Type is AvrAsmLine.LineType.Comment or AvrAsmLine.LineType.Empty) ++j2;
                if (j2 >= result.Count || result[j2].Type != AvrAsmLine.LineType.Instruction) continue;
                
                var k2 = j2 + 1;
                while (k2 < result.Count && result[k2].Type is AvrAsmLine.LineType.Comment or AvrAsmLine.LineType.Empty) ++k2;
                if (k2 >= result.Count || result[k2].Type != AvrAsmLine.LineType.Instruction) continue;

                var a2 = result[i];
                var b2 = result[j2];
                var c2 = result[k2];

                if (a2.Mnemonic != "MOV" || c2.Mnemonic != "MOV" ||
                    b2.Mnemonic is not ("INC" or "DEC" or "COM" or "NEG") ||
                    b2.Op1 != a2.Op1 || c2.Op2 != a2.Op1 || c2.Op1 != a2.Op2) continue;

                var ra = a2.Op1; // Ej. R24
                var rb = a2.Op2; // Ej. R4

                result[i] = AvrAsmLine.MakeEmpty(); 
                result[j2] = AvrAsmLine.MakeInstruction(b2.Mnemonic, rb); // INC R4
                result[k2] = AvrAsmLine.MakeInstruction("MOV", ra, rb);   // MOV R24, R4

                win3 = true;
            }

            result.RemoveAll(l => l.Type == AvrAsmLine.LineType.Empty);
        }

        result.RemoveAll(l => l.Type == AvrAsmLine.LineType.Empty);
        return result;
    }
}