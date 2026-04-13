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

namespace PyMCU.Backend.Targets.PIO;

public class PIOAsmLine
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
    public int Delay = 0;

    public static PIOAsmLine MakeInstruction(string m, string o1 = "", string o2 = "", string o3 = "")
        => new() { Type = LineType.Instruction, Mnemonic = m, Op1 = o1, Op2 = o2, Op3 = o3 };

    public static PIOAsmLine MakeLabel(string l)
        => new() { Type = LineType.Label, LabelText = l };

    public static PIOAsmLine MakeComment(string c)
        => new() { Type = LineType.Comment, Content = c };

    public static PIOAsmLine MakeRaw(string r)
        => new() { Type = LineType.Raw, Content = r };

    public static PIOAsmLine MakeEmpty()
        => new() { Type = LineType.Empty };

    public override string ToString()
    {
        switch (Type)
        {
            case LineType.Instruction:
            {
                string s = "    " + Mnemonic;
                if (!string.IsNullOrEmpty(Op1))
                {
                    s += " " + Op1;
                    if (!string.IsNullOrEmpty(Op2))
                    {
                        s += ", " + Op2;
                        if (!string.IsNullOrEmpty(Op3))
                            s += ", " + Op3;
                    }
                }

                if (Delay > 0) s += $" [{Delay}]";
                return s;
            }
            case LineType.Label: return $"{LabelText}:";
            case LineType.Comment: return $"; {Content}";
            case LineType.Raw: return Content;
            default: return "";
        }
    }
}

public static class PIOPeephole
{
    public static List<PIOAsmLine> Optimize(List<PIOAsmLine> lines)
    {
        var result = new List<PIOAsmLine>();
        string? xVal = null;
        string? yVal = null;

        foreach (var line in lines)
        {
            if (line.Type == PIOAsmLine.LineType.Instruction)
            {
                // Remove redundant MOV
                if (line.Mnemonic == "MOV" && line.Op1 == line.Op2) continue;

                if (line.Mnemonic == "SET")
                {
                    if (line.Op1 == "X")
                    {
                        if (xVal == line.Op2) continue;
                        xVal = line.Op2;
                    }
                    else if (line.Op1 == "Y")
                    {
                        if (yVal == line.Op2) continue;
                        yVal = line.Op2;
                    }
                }
                else if (line.Mnemonic == "MOV")
                {
                    if (line.Op1 == "X")
                    {
                        if (line.Op2 == "Y" && xVal != null && yVal != null && xVal == yVal) continue;
                        xVal = null;
                    }
                    else if (line.Op1 == "Y")
                    {
                        if (line.Op2 == "X" && xVal != null && yVal != null && xVal == yVal) continue;
                        yVal = null;
                    }
                }
                else
                {
                    xVal = null;
                    yVal = null;
                }
            }
            else if (line.Type == PIOAsmLine.LineType.Label)
            {
                xVal = null;
                yVal = null;
            }

            result.Add(line);
        }

        return result;
    }
}