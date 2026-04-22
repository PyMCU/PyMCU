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

namespace PyMCU.Backend.Targets.PIC18;

public class PIC18AsmLine
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

    public static PIC18AsmLine MakeInstruction(string m, string o1 = "", string o2 = "", string o3 = "")
        => new() { Type = LineType.Instruction, Mnemonic = m, Op1 = o1, Op2 = o2, Op3 = o3 };

    public static PIC18AsmLine MakeLabel(string l)
        => new() { Type = LineType.Label, LabelText = l };

    public static PIC18AsmLine MakeComment(string c)
        => new() { Type = LineType.Comment, Content = c };

    public static PIC18AsmLine MakeRaw(string r)
        => new() { Type = LineType.Raw, Content = r };

    public static PIC18AsmLine MakeEmpty()
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

public static class PIC18Peephole
{
    public static List<PIC18AsmLine> Optimize(List<PIC18AsmLine> lines)
    {
        var source = new List<PIC18AsmLine>(lines);
        var result = new List<PIC18AsmLine>();
        bool changed = true;

        while (changed)
        {
            changed = false;
            result.Clear();

            int? currentBsr = null;
            bool deadCodeMode = false;

            foreach (var current in source)
            {
                if (current.Type == PIC18AsmLine.LineType.Label)
                {
                    deadCodeMode = false;
                    currentBsr = null;
                    result.Add(current);
                    continue;
                }

                if (deadCodeMode)
                {
                    if (current.Type == PIC18AsmLine.LineType.Instruction)
                    {
                        changed = true;
                        continue;
                    }
                }

                if (current.Type != PIC18AsmLine.LineType.Instruction)
                {
                    result.Add(current);
                    continue;
                }

                if (current.Mnemonic == "MOVFF" && current.Op1 == current.Op2)
                {
                    changed = true;
                    continue;
                }

                if (current.Mnemonic == "MOVLB")
                {
                    if (int.TryParse(current.Op1, out int bank))
                    {
                        if (currentBsr.HasValue && currentBsr.Value == bank)
                        {
                            changed = true;
                            continue;
                        }

                        currentBsr = bank;
                    }
                    else currentBsr = null;
                }

                if (current.Mnemonic is "RETURN" or "RETFIE" or "GOTO" or "BRA")
                    deadCodeMode = true;

                result.Add(current);
            }

            if (changed) source = new List<PIC18AsmLine>(result);
        }

        return result;
    }
}