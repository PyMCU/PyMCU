// SPDX-License-Identifier: MIT
// PyMCU PIC Backend — assembly-line representation shared by PIC12, PIC14, and PIC18.

namespace PyMCU.Backend.Targets.PIC;

/// <summary>
/// Represents a single line in the generated gpasm-compatible PIC assembly output.
/// </summary>
public sealed class PicAsmLine
{
    public enum LineKind { Instruction, Label, Comment, Raw, Empty }

    public LineKind Kind;
    public string Content = "";

    // Factory helpers --------------------------------------------------------

    /// <summary>Instruction with optional operands already concatenated.</summary>
    public static PicAsmLine MakeInstruction(string content)
        => new() { Kind = LineKind.Instruction, Content = content };

    public static PicAsmLine MakeLabel(string label)
        => new() { Kind = LineKind.Label, Content = label };

    public static PicAsmLine MakeComment(string comment)
        => new() { Kind = LineKind.Comment, Content = comment };

    public static PicAsmLine MakeRaw(string text)
        => new() { Kind = LineKind.Raw, Content = text };

    public static PicAsmLine MakeEmpty()
        => new() { Kind = LineKind.Empty };

    // Rendering --------------------------------------------------------------

    public override string ToString() => Kind switch
    {
        LineKind.Instruction => $"\t{Content}",
        LineKind.Label       => $"{Content}:",
        LineKind.Comment     => $"; {Content}",
        LineKind.Raw         => Content,
        _                    => ""
    };
}
