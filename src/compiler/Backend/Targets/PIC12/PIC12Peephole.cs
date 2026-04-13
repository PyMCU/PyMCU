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

namespace PyMCU.Backend.Targets.PIC12;

public class PIC12AsmLine
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

    public static PIC12AsmLine MakeInstruction(string m, string o1 = "", string o2 = "")
        => new() { Type = LineType.Instruction, Mnemonic = m, Op1 = o1, Op2 = o2 };

    public static PIC12AsmLine MakeLabel(string l)
        => new() { Type = LineType.Label, LabelText = l };

    public static PIC12AsmLine MakeComment(string c)
        => new() { Type = LineType.Comment, Content = c };

    public static PIC12AsmLine MakeRaw(string r)
        => new() { Type = LineType.Raw, Content = r };

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

public static class PIC12Peephole
{
    public static List<PIC12AsmLine> Optimize(List<PIC12AsmLine> lines)
    {
        var result = new List<PIC12AsmLine>(lines);
        bool changed = true;

        while (changed)
        {
            changed = false;
            var next = new List<PIC12AsmLine>();

            string? wLit = null;
            string? wVar = null;

            foreach (var current in result)
            {
                if (current.Type == PIC12AsmLine.LineType.Label)
                {
                    wLit = null;
                    wVar = null;
                    next.Add(current);
                    continue;
                }

                if (current.Type != PIC12AsmLine.LineType.Instruction)
                {
                    next.Add(current);
                    continue;
                }

                // Redundant MOVLW
                if (current.Mnemonic == "MOVLW")
                {
                    if (wLit == current.Op1)
                    {
                        changed = true;
                        continue;
                    }

                    wLit = current.Op1;
                    wVar = null;
                }
                else if (current.Mnemonic == "MOVF" && current.Op2 == "W")
                {
                    if (wVar == current.Op1)
                    {
                        changed = true;
                        continue;
                    }

                    wVar = current.Op1;
                    wLit = null;
                }
                else if (current.Mnemonic == "MOVWF")
                {
                    if (wVar == current.Op1)
                    {
                        changed = true;
                        continue;
                    }

                    wVar = current.Op1;
                }
                else if (current.Mnemonic == "GOTO" || current.Mnemonic == "CALL" ||
                         current.Mnemonic == "RETLW")
                {
                    wLit = null;
                    wVar = null;
                }
                else
                {
                    wLit = null;
                    wVar = null;
                }

                next.Add(current);
            }

            if (changed) result = next;
        }

        return result;
    }
}