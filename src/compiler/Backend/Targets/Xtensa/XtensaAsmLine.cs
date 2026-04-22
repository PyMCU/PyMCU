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

namespace PyMCU.Backend.Targets.Xtensa;

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
            case LineType.Label: return $"{LabelText}:";
            case LineType.Comment: return $"# {Content}";
            case LineType.Raw: return Content;
            default: return "";
        }
    }
}

public static class XtensaPeephole
{
    public static List<XtensaAsmLine> Optimize(List<XtensaAsmLine> lines)
    {
        var result = new List<XtensaAsmLine>(lines);
        bool changed = true;

        while (changed)
        {
            changed = false;
            var next = new List<XtensaAsmLine>();

            string? a8Val = null;
            string? a9Val = null;
            string? a2Val = null;

            foreach (var current in result)
            {
                if (current.Type == XtensaAsmLine.LineType.Label)
                {
                    a8Val = null;
                    a9Val = null;
                    a2Val = null;
                    next.Add(current);
                    continue;
                }

                if (current.Type != XtensaAsmLine.LineType.Instruction)
                {
                    next.Add(current);
                    continue;
                }

                // Eliminate redundant movi: same register, same constant
                if (current.Mnemonic == "movi")
                {
                    if (current.Op1 == "a8" && a8Val == current.Op2)
                    {
                        changed = true;
                        continue;
                    }

                    if (current.Op1 == "a9" && a9Val == current.Op2)
                    {
                        changed = true;
                        continue;
                    }

                    if (current.Op1 == "a2" && a2Val == current.Op2)
                    {
                        changed = true;
                        continue;
                    }

                    if (current.Op1 == "a8") a8Val = current.Op2;
                    else if (current.Op1 == "a9") a9Val = current.Op2;
                    else if (current.Op1 == "a2") a2Val = current.Op2;
                }
                else if (current.Mnemonic is "l32i" or "s32i" or "addi" or "l32r")
                {
                    if (current.Op1 == "a8") a8Val = null;
                    if (current.Op1 == "a9") a9Val = null;
                    if (current.Op1 == "a2") a2Val = null;
                }
                else if (current.Mnemonic is "call0" or "callx0" or "j" or "ret.n" or "ret")
                {
                    a8Val = null;
                    a9Val = null;
                    a2Val = null;
                }
                else
                {
                    if (current.Op1 == "a8") a8Val = null;
                    if (current.Op1 == "a9") a9Val = null;
                    if (current.Op1 == "a2") a2Val = null;
                }

                next.Add(current);
            }

            if (result.Count == next.Count && !changed) break;
            result = next;
        }

        return result;
    }
}
