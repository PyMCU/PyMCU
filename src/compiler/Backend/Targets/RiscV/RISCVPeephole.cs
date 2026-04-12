namespace PyMCU.Backend.Targets.RiscV;

public class RISCVAsmLine
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
    public string Op3 = "";
    public string Content = "";

    public static RISCVAsmLine MakeInstruction(string m, string o1 = "", string o2 = "", string o3 = "")
        => new() { Type = LineType.Instruction, Mnemonic = m, Op1 = o1, Op2 = o2, Op3 = o3 };

    public static RISCVAsmLine MakeLabel(string l)
        => new() { Type = LineType.Label, LabelText = l };

    public static RISCVAsmLine MakeComment(string c)
        => new() { Type = LineType.Comment, Content = c };

    public static RISCVAsmLine MakeRaw(string r)
        => new() { Type = LineType.Raw, Content = r };

    public static RISCVAsmLine MakeEmpty()
        => new() { Type = LineType.Empty };

    public override string ToString()
    {
        switch (Type)
        {
            case LineType.Instruction:
                if (string.IsNullOrEmpty(Op3))
                {
                    if (string.IsNullOrEmpty(Op2))
                    {
                        if (string.IsNullOrEmpty(Op1)) return $"\t{Mnemonic}";
                        return $"\t{Mnemonic}\t{Op1}";
                    }

                    return $"\t{Mnemonic}\t{Op1}, {Op2}";
                }

                return $"\t{Mnemonic}\t{Op1}, {Op2}, {Op3}";
            case LineType.Label: return $"{LabelText}:";
            case LineType.Comment: return $"# {Content}";
            case LineType.Raw: return Content;
            default: return "";
        }
    }
}

public static class RiscvPeephole
{
    public static List<RISCVAsmLine> Optimize(List<RISCVAsmLine> lines)
    {
        var result = new List<RISCVAsmLine>(lines);
        bool changed = true;

        while (changed)
        {
            changed = false;
            var next = new List<RISCVAsmLine>();

            string? t0Val = null;
            string? t1Val = null;
            string? a0Val = null;

            foreach (var current in result)
            {
                if (current.Type == RISCVAsmLine.LineType.Label)
                {
                    t0Val = null;
                    t1Val = null;
                    a0Val = null;
                    next.Add(current);
                    continue;
                }

                if (current.Type != RISCVAsmLine.LineType.Instruction)
                {
                    next.Add(current);
                    continue;
                }

                if (current.Mnemonic == "li")
                {
                    if (current.Op1 == "t0" && t0Val == current.Op2)
                    {
                        changed = true;
                        continue;
                    }

                    if (current.Op1 == "t1" && t1Val == current.Op2)
                    {
                        changed = true;
                        continue;
                    }

                    if (current.Op1 == "a0" && a0Val == current.Op2)
                    {
                        changed = true;
                        continue;
                    }

                    if (current.Op1 == "t0") t0Val = current.Op2;
                    else if (current.Op1 == "t1") t1Val = current.Op2;
                    else if (current.Op1 == "a0") a0Val = current.Op2;
                }
                else if (current.Mnemonic is "lw" or "sw" or "addi")
                {
                    if (current.Op1 == "t0") t0Val = null;
                    if (current.Op1 == "t1") t1Val = null;
                    if (current.Op1 == "a0") a0Val = null;
                }
                else if (current.Mnemonic is "call" or "j" or "ret")
                {
                    t0Val = null;
                    t1Val = null;
                    a0Val = null;
                }
                else
                {
                    if (current.Op1 == "t0") t0Val = null;
                    if (current.Op1 == "t1") t1Val = null;
                    if (current.Op1 == "a0") a0Val = null;
                }

                next.Add(current);
            }

            if (result.Count == next.Count && !changed) break;
            result = next;
        }

        return result;
    }
}