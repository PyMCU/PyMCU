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
    // Scratch registers whose last known immediate is worth remembering. t2 is
    // the address scratch every MMIO access goes through, so back-to-back reads
    // and writes of the same peripheral register reload it constantly.
    private static readonly string[] TrackedRegs = ["t0", "t1", "t2", "a0"];

    // Anything that transfers control leaves the tracked values unknown at the
    // point execution resumes.
    private static bool IsControlTransfer(string mnemonic)
        => mnemonic is "call" or "jal" or "jalr" or "j" or "jr" or "ret" or "mret" or "tail";

    public static List<RISCVAsmLine> Optimize(List<RISCVAsmLine> lines)
    {
        var result = new List<RISCVAsmLine>(lines);

        // Each pass can expose work for the others (forwarding a slot leaves a
        // move that may become redundant), so they run until nothing shrinks.
        for (int round = 0; round < 8; round++)
        {
            int before = result.Count;
            result = ForwardFrameSlotStores(result);
            result = DropRedundantImmediates(result);
            result = DropSelfMoves(result);
            if (result.Count == before) break;
        }

        return result;
    }

    // A frame slot is never volatile: a load that immediately follows the store
    // that filled it can reuse the value the store already had in a register.
    // Only s0-relative slots qualify — an MMIO address behind t2 must keep both
    // accesses, because reading a peripheral back is not the same as the value
    // just written.
    private static bool IsFrameSlot(string operand) => operand.EndsWith("(s0)");

    private static List<RISCVAsmLine> ForwardFrameSlotStores(List<RISCVAsmLine> lines)
    {
        var drop = new bool[lines.Count];
        var rewrite = new RISCVAsmLine?[lines.Count];

        for (int i = 0; i < lines.Count; i++)
        {
            var store = lines[i];
            if (store.Type != RISCVAsmLine.LineType.Instruction) continue;
            if (store.Mnemonic != "sw" || !IsFrameSlot(store.Op2)) continue;

            // Comments may sit between the two, but a label means control can
            // arrive here from somewhere the store never ran.
            int j = i + 1;
            while (j < lines.Count &&
                   lines[j].Type is RISCVAsmLine.LineType.Comment or RISCVAsmLine.LineType.Empty)
                j++;

            if (j >= lines.Count) continue;
            var load = lines[j];
            if (load.Type != RISCVAsmLine.LineType.Instruction) continue;
            if (load.Mnemonic != "lw" || load.Op2 != store.Op2) continue;

            if (load.Op1 == store.Op1)
                drop[j] = true;
            else
                rewrite[j] = RISCVAsmLine.MakeInstruction("mv", load.Op1, store.Op1);
        }

        var result = new List<RISCVAsmLine>(lines.Count);
        for (int i = 0; i < lines.Count; i++)
        {
            if (drop[i]) continue;
            result.Add(rewrite[i] ?? lines[i]);
        }

        return result;
    }

    private static List<RISCVAsmLine> DropSelfMoves(List<RISCVAsmLine> lines)
    {
        var result = new List<RISCVAsmLine>(lines.Count);
        foreach (var line in lines)
        {
            if (line.Type == RISCVAsmLine.LineType.Instruction &&
                line.Mnemonic == "mv" && line.Op1 == line.Op2)
                continue;
            result.Add(line);
        }

        return result;
    }

    private static List<RISCVAsmLine> DropRedundantImmediates(List<RISCVAsmLine> lines)
    {
        var result = new List<RISCVAsmLine>(lines);
        bool changed = true;

        while (changed)
        {
            changed = false;
            var next = new List<RISCVAsmLine>();
            var known = new Dictionary<string, string?>();

            foreach (var current in result)
            {
                // A label is a join point: control may arrive from anywhere.
                if (current.Type == RISCVAsmLine.LineType.Label)
                {
                    known.Clear();
                    next.Add(current);
                    continue;
                }

                if (current.Type != RISCVAsmLine.LineType.Instruction)
                {
                    next.Add(current);
                    continue;
                }

                if (current.Mnemonic == "li" && Array.IndexOf(TrackedRegs, current.Op1) >= 0)
                {
                    if (known.TryGetValue(current.Op1, out var held) && held == current.Op2)
                    {
                        changed = true;
                        continue;
                    }

                    known[current.Op1] = current.Op2;
                }
                else if (IsControlTransfer(current.Mnemonic))
                {
                    known.Clear();
                }
                else
                {
                    // Op1 is the destination for every other instruction the
                    // backend emits, except stores, where invalidating is merely
                    // conservative.
                    known.Remove(current.Op1);
                }

                next.Add(current);
            }

            result = next;
        }

        return result;
    }
}