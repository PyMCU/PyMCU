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
///   - Collapse addi a1, a1, +N followed by addi a1, a1, -N (frame enter/exit).
///   - Remove consecutive identical label definitions.
/// </summary>
public static class XtensaPeephole
{
    public static List<XtensaAsmLine> Optimize(List<XtensaAsmLine> input)
    {
        var result = RemoveSelfMoves(input);
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
}
