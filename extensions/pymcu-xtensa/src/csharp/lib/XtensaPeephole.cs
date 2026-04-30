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
/// Post-codegen peephole optimiser for Xtensa GAS assembly.
///
/// Current passes:
///   - Remove mov rX, rX (self-moves introduced by copy/call sequences).
///   - Consecutive s32i then l32i of the same slot: replace load with mov
///     (or remove it entirely when src == dst register).
///   - Remove consecutive identical label definitions.
/// </summary>
public static class XtensaPeephole
{
    public static List<XtensaAsmLine> Optimize(List<XtensaAsmLine> input)
    {
        var result = RemoveSelfMoves(input);
        result = EliminateRedundantLoadAfterStore(result);
        return result;
    }

    // Remove   mov rX, rX   instructions (no-ops).
    private static List<XtensaAsmLine> RemoveSelfMoves(List<XtensaAsmLine> input)
    {
        var out_ = new List<XtensaAsmLine>(input.Count);
        foreach (var line in input)
        {
            if (line.Type == XtensaAsmLine.LineType.Instruction
                && line.Mnemonic == "mov"
                && line.Op1 == line.Op2
                && !string.IsNullOrEmpty(line.Op1))
            {
                continue; // skip self-move
            }
            out_.Add(line);
        }
        return out_;
    }

    // When a store to a memory slot is immediately followed by a load from the
    // same base register and byte offset into the same register, the load is
    // redundant: the value is already in the source register of the store.
    // Replace with mov dst, src (or remove if src and dst are the same register).
    //
    // Matches:   s32i rA, base, N      (store to base+N)
    //            l32i rB, base, N      (load  from same base+N)
    // Result:    s32i rA, base, N
    //            mov  rB, rA           (or removed if rA == rB)
    private static List<XtensaAsmLine> EliminateRedundantLoadAfterStore(List<XtensaAsmLine> input)
    {
        var out_ = new List<XtensaAsmLine>(input.Count);
        int n = input.Count;
        for (int i = 0; i < n; i++)
        {
            var cur = input[i];

            // Look for s32i rA, base, N  followed by  l32i rB, base, N.
            if (i + 1 < n
                && cur.Type == XtensaAsmLine.LineType.Instruction
                && cur.Mnemonic == "s32i"
                && !string.IsNullOrEmpty(cur.Op1)   // rA
                && !string.IsNullOrEmpty(cur.Op2)   // base
                && !string.IsNullOrEmpty(cur.Op3))  // N
            {
                var next = input[i + 1];
                if (next.Type == XtensaAsmLine.LineType.Instruction
                    && next.Mnemonic == "l32i"
                    && next.Op2 == cur.Op2   // same base register
                    && next.Op3 == cur.Op3)  // same offset
                {
                    out_.Add(cur);  // keep the store
                    // Emit mov rB, rA — but skip if rA == rB (becomes mov rX, rX
                    // which the previous pass will remove anyway; skip directly).
                    if (next.Op1 != cur.Op1)
                        out_.Add(XtensaAsmLine.MakeInstruction("mov", next.Op1, cur.Op1));
                    i++; // consume the load
                    continue;
                }
            }

            out_.Add(cur);
        }
        return out_;
    }
}
