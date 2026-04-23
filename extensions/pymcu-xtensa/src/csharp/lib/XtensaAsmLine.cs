/*
 * -----------------------------------------------------------------------------
 * PyMCU — pymcu-xtensa extension
 * Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
 *
 * SPDX-License-Identifier: MIT
 * -----------------------------------------------------------------------------
 */

namespace PyMCU.Backend.Targets.Xtensa;

/// <summary>
/// Represents a single line of Xtensa GAS assembly output.
/// Supports up to four operands (required for instructions such as extui).
/// </summary>
public class XtensaAsmLine
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
    public string Op4 = "";
    public string Content = "";

    public static XtensaAsmLine MakeInstruction(string m, string o1 = "", string o2 = "", string o3 = "", string o4 = "")
        => new() { Type = LineType.Instruction, Mnemonic = m, Op1 = o1, Op2 = o2, Op3 = o3, Op4 = o4 };

    public static XtensaAsmLine MakeLabel(string l)
        => new() { Type = LineType.Label, LabelText = l };

    public static XtensaAsmLine MakeComment(string c)
        => new() { Type = LineType.Comment, Content = c };

    public static XtensaAsmLine MakeRaw(string r)
        => new() { Type = LineType.Raw, Content = r };

    public static XtensaAsmLine MakeEmpty()
        => new() { Type = LineType.Empty };

    public override string ToString()
    {
        switch (Type)
        {
            case LineType.Instruction:
                if (string.IsNullOrEmpty(Op4))
                {
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
                }
                return $"\t{Mnemonic}\t{Op1}, {Op2}, {Op3}, {Op4}";
            case LineType.Label:   return $"{LabelText}:";
            case LineType.Comment: return $"# {Content}";
            case LineType.Raw:     return Content;
            default:               return "";
        }
    }
}
