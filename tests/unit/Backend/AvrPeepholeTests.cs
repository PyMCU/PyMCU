using PyMCU.Backend.Targets.AVR;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// Unit tests for AvrPeephole.Optimize() — all patterns are tested directly
/// against the static method without going through the full AVR code-gen
/// pipeline, so failures pinpoint individual peephole rules.
/// </summary>
public class AvrPeepholeTests
{
    private static List<AvrAsmLine> Opt(params AvrAsmLine[] lines)
        => AvrPeephole.Optimize(lines.ToList());

    // ─── Redundant LDI ───────────────────────────────────────────────────────

    [Fact]
    public void RedundantLDI_SameRegAndValue_SecondEliminated()
    {
        var result = Opt(
            AvrAsmLine.MakeInstruction("LDI", "R16", "5"),
            AvrAsmLine.MakeInstruction("LDI", "R16", "5"));

        int count = result.Count(l =>
            l.Type == AvrAsmLine.LineType.Instruction &&
            l.Mnemonic == "LDI" && l.Op1 == "R16" && l.Op2 == "5");
        Assert.Equal(1, count);
    }

    [Fact]
    public void RedundantLDI_DifferentValues_BothKept()
    {
        var result = Opt(
            AvrAsmLine.MakeInstruction("LDI", "R16", "1"),
            AvrAsmLine.MakeInstruction("LDI", "R16", "2"));

        Assert.Contains(result, l => l.Mnemonic == "LDI" && l.Op2 == "1");
        Assert.Contains(result, l => l.Mnemonic == "LDI" && l.Op2 == "2");
    }

    [Fact]
    public void RedundantLDI_DifferentRegisters_BothKept()
    {
        var result = Opt(
            AvrAsmLine.MakeInstruction("LDI", "R16", "7"),
            AvrAsmLine.MakeInstruction("LDI", "R17", "7"));

        Assert.Contains(result, l => l.Mnemonic == "LDI" && l.Op1 == "R16");
        Assert.Contains(result, l => l.Mnemonic == "LDI" && l.Op1 == "R17");
    }

    // ─── Redundant MOV ────────────────────────────────────────────────────────

    [Fact]
    public void RedundantMOV_SameAliases_SecondEliminated()
    {
        // After LDI R24, 5 and MOV R4, R24, the alias for R4 matches R24's alias.
        // A second MOV R4, R24 is redundant.
        var result = Opt(
            AvrAsmLine.MakeInstruction("LDI", "R24", "5"),
            AvrAsmLine.MakeInstruction("MOV", "R4", "R24"),
            AvrAsmLine.MakeInstruction("MOV", "R4", "R24"));

        int count = result.Count(l =>
            l.Type == AvrAsmLine.LineType.Instruction &&
            l.Mnemonic == "MOV" && l.Op1 == "R4" && l.Op2 == "R24");
        Assert.Equal(1, count);
    }

    [Fact]
    public void MOV_AfterArithmetic_NotEliminated()
    {
        // ADD modifies R24's alias; the following MOV must be retained.
        var result = Opt(
            AvrAsmLine.MakeInstruction("LDI", "R24", "1"),
            AvrAsmLine.MakeInstruction("MOV", "R4", "R24"),
            AvrAsmLine.MakeInstruction("ADD", "R24", "R4"),
            AvrAsmLine.MakeInstruction("MOV", "R4", "R24"));

        // Final MOV must still be present
        int lastMovCount = result.Count(l =>
            l.Type == AvrAsmLine.LineType.Instruction &&
            l.Mnemonic == "MOV" && l.Op1 == "R4" && l.Op2 == "R24");
        Assert.True(lastMovCount >= 1);
    }

    // ─── Dead Label Elimination ───────────────────────────────────────────────

    [Fact]
    public void DeadLabel_Unreferenced_L_Prefix_Eliminated()
    {
        // L_0 is not referenced by any branch — must be removed.
        var result = Opt(
            AvrAsmLine.MakeInstruction("LDI", "R24", "1"),
            AvrAsmLine.MakeLabel("L_0"),
            AvrAsmLine.MakeInstruction("RET"));

        Assert.DoesNotContain(result, l =>
            l.Type == AvrAsmLine.LineType.Label && l.LabelText == "L_0");
    }

    [Fact]
    public void DeadLabel_Unreferenced_LDot_Prefix_Eliminated()
    {
        var result = Opt(
            AvrAsmLine.MakeInstruction("RET"),
            AvrAsmLine.MakeLabel("L.unreachable"));

        Assert.DoesNotContain(result, l =>
            l.Type == AvrAsmLine.LineType.Label && l.LabelText == "L.unreachable");
    }

    [Fact]
    public void DeadLabel_ReferencedByBranch_Kept()
    {
        // L_target is referenced; an intervening instruction prevents RJMP-to-next elimination.
        var result = Opt(
            AvrAsmLine.MakeInstruction("RJMP", "L_target"),
            AvrAsmLine.MakeInstruction("LDI", "R24", "0"),   // instruction between RJMP and label
            AvrAsmLine.MakeLabel("L_target"),
            AvrAsmLine.MakeInstruction("RET"));

        Assert.Contains(result, l =>
            l.Type == AvrAsmLine.LineType.Label && l.LabelText == "L_target");
    }

    [Fact]
    public void DeadLabel_NonL_Prefix_Kept()
    {
        // Labels not starting with L. / L_ (e.g. function names) must never be removed.
        var result = Opt(
            AvrAsmLine.MakeLabel("main"),
            AvrAsmLine.MakeInstruction("RET"));

        Assert.Contains(result, l =>
            l.Type == AvrAsmLine.LineType.Label && l.LabelText == "main");
    }

    // ─── Pattern A: STD Y+N, Rx followed immediately by LDD Ry, Y+N ─────────

    [Fact]
    public void StdLdd_SameOffsetAndReg_LddEliminated()
    {
        // STD Y+2, R20 ; LDD R20, Y+2 → LDD is dead (R20 already holds the value)
        var result = Opt(
            AvrAsmLine.MakeInstruction("STD", "Y+2", "R20"),
            AvrAsmLine.MakeInstruction("LDD", "R20", "Y+2"));

        Assert.DoesNotContain(result, l => l.Mnemonic == "LDD");
    }

    [Fact]
    public void StdLdd_SameOffsetDifferentReg_LddReplacedWithMov()
    {
        // STD Y+2, R20 ; LDD R22, Y+2 → MOV R22, R20 (avoids a memory round-trip)
        var result = Opt(
            AvrAsmLine.MakeInstruction("STD", "Y+2", "R20"),
            AvrAsmLine.MakeInstruction("LDD", "R22", "Y+2"));

        Assert.DoesNotContain(result, l => l.Mnemonic == "LDD");
        Assert.Contains(result, l =>
            l.Mnemonic == "MOV" && l.Op1 == "R22" && l.Op2 == "R20");
    }

    [Fact]
    public void StdLdd_DifferentOffsets_LddKept()
    {
        // Offsets differ → no optimisation should occur.
        var result = Opt(
            AvrAsmLine.MakeInstruction("STD", "Y+2", "R20"),
            AvrAsmLine.MakeInstruction("LDD", "R22", "Y+4"));

        Assert.Contains(result, l => l.Mnemonic == "LDD" && l.Op2 == "Y+4");
    }

    // ─── Pattern B: LDD Rx, Y+N followed immediately by STD Y+N, Rx ─────────

    [Fact]
    public void LddStd_SameOffsetAndReg_StdEliminated()
    {
        // LDD R20, Y+3 ; STD Y+3, R20 → STD is a no-op (memory unchanged)
        var result = Opt(
            AvrAsmLine.MakeInstruction("LDD", "R20", "Y+3"),
            AvrAsmLine.MakeInstruction("STD", "Y+3", "R20"));

        Assert.DoesNotContain(result, l =>
            l.Mnemonic == "STD" && l.Op1 == "Y+3" && l.Op2 == "R20");
    }

    [Fact]
    public void LddStd_DifferentReg_StdKept()
    {
        // LDD R20, Y+3 ; STD Y+3, R22 → R22 ≠ R20, so STD is NOT a no-op.
        var result = Opt(
            AvrAsmLine.MakeInstruction("LDD", "R20", "Y+3"),
            AvrAsmLine.MakeInstruction("STD", "Y+3", "R22"));

        Assert.Contains(result, l =>
            l.Mnemonic == "STD" && l.Op1 == "Y+3" && l.Op2 == "R22");
    }

    // ─── RJMP to immediately following label ─────────────────────────────────

    [Fact]
    public void RjmpToNextLabel_Eliminated()
    {
        // RJMP L_end ; L_end: → the jump is to the very next label (no-op)
        var result = Opt(
            AvrAsmLine.MakeInstruction("RJMP", "L_end"),
            AvrAsmLine.MakeLabel("L_end"),
            AvrAsmLine.MakeInstruction("RET"));

        Assert.DoesNotContain(result, l => l.Mnemonic == "RJMP" && l.Op1 == "L_end");
    }

    [Fact]
    public void RjmpToNextLabel_WithInterveningComments_Eliminated()
    {
        // Comments between RJMP and its target label must not block the optimisation.
        var result = Opt(
            AvrAsmLine.MakeInstruction("RJMP", "L_skip"),
            AvrAsmLine.MakeComment("some comment"),
            AvrAsmLine.MakeLabel("L_skip"),
            AvrAsmLine.MakeInstruction("RET"));

        Assert.DoesNotContain(result, l => l.Mnemonic == "RJMP" && l.Op1 == "L_skip");
    }

    [Fact]
    public void RjmpToNonNextLabel_Kept()
    {
        // RJMP main targets a label that is not the immediately following one.
        var result = Opt(
            AvrAsmLine.MakeInstruction("RJMP", "main"),
            AvrAsmLine.MakeInstruction("LDI", "R24", "1"),
            AvrAsmLine.MakeLabel("main"),
            AvrAsmLine.MakeInstruction("RET"));

        Assert.Contains(result, l => l.Mnemonic == "RJMP" && l.Op1 == "main");
    }

    // ─── 3-window: MOV Ra, Rb ; OP Ra ; MOV Rb, Ra → OP Rb ; MOV Ra, Rb ─────

    [Fact]
    public void MovIncMov_CollapsedToIncOnSource()
    {
        // MOV R24, R4 ; INC R24 ; MOV R4, R24 → INC R4 ; MOV R24, R4
        var result = Opt(
            AvrAsmLine.MakeInstruction("MOV", "R24", "R4"),
            AvrAsmLine.MakeInstruction("INC", "R24"),
            AvrAsmLine.MakeInstruction("MOV", "R4", "R24"));

        // INC must now target R4 (the original source register)
        Assert.Contains(result, l => l.Mnemonic == "INC" && l.Op1 == "R4");
        // The original copy-out (MOV R4, R24) must be gone — it was replaced by MOV R24, R4
        Assert.DoesNotContain(result, l =>
            l.Mnemonic == "MOV" && l.Op1 == "R4" && l.Op2 == "R24");
    }

    [Fact]
    public void MovDecMov_CollapsedToDecOnSource()
    {
        // MOV R24, R5 ; DEC R24 ; MOV R5, R24 → DEC R5 ; MOV R24, R5
        var result = Opt(
            AvrAsmLine.MakeInstruction("MOV", "R24", "R5"),
            AvrAsmLine.MakeInstruction("DEC", "R24"),
            AvrAsmLine.MakeInstruction("MOV", "R5", "R24"));

        Assert.Contains(result, l => l.Mnemonic == "DEC" && l.Op1 == "R5");
    }

    [Fact]
    public void MovComMov_CollapsedToComOnSource()
    {
        // COM (bitwise complement)
        var result = Opt(
            AvrAsmLine.MakeInstruction("MOV", "R24", "R6"),
            AvrAsmLine.MakeInstruction("COM", "R24"),
            AvrAsmLine.MakeInstruction("MOV", "R6", "R24"));

        Assert.Contains(result, l => l.Mnemonic == "COM" && l.Op1 == "R6");
    }

    [Fact]
    public void MovNegMov_CollapsedToNegOnSource()
    {
        var result = Opt(
            AvrAsmLine.MakeInstruction("MOV", "R24", "R7"),
            AvrAsmLine.MakeInstruction("NEG", "R24"),
            AvrAsmLine.MakeInstruction("MOV", "R7", "R24"));

        Assert.Contains(result, l => l.Mnemonic == "NEG" && l.Op1 == "R7");
    }

    // ─── Idempotency & edge cases ─────────────────────────────────────────────

    [Fact]
    public void EmptyInput_ReturnsEmpty()
        => Assert.Empty(AvrPeephole.Optimize(new List<AvrAsmLine>()));

    [Fact]
    public void CommentsAndRaw_PassThrough()
    {
        var result = Opt(
            AvrAsmLine.MakeComment("hello"),
            AvrAsmLine.MakeRaw(".equ RAMSTART, 0x0100"));

        Assert.Contains(result, l => l.Type == AvrAsmLine.LineType.Comment);
        Assert.Contains(result, l => l.Type == AvrAsmLine.LineType.Raw);
    }

    [Fact]
    public void AvrAsmLine_ToString_Instruction_NoOps()
    {
        var line = AvrAsmLine.MakeInstruction("RET");
        Assert.Equal("\tRET", line.ToString());
    }

    [Fact]
    public void AvrAsmLine_ToString_Instruction_OneOp()
    {
        var line = AvrAsmLine.MakeInstruction("RJMP", "main");
        Assert.Equal("\tRJMP\tmain", line.ToString());
    }

    [Fact]
    public void AvrAsmLine_ToString_Instruction_TwoOps()
    {
        var line = AvrAsmLine.MakeInstruction("LDI", "R24", "42");
        Assert.Equal("\tLDI\tR24, 42", line.ToString());
    }

    [Fact]
    public void AvrAsmLine_ToString_Label()
    {
        var line = AvrAsmLine.MakeLabel("main");
        Assert.Equal("main:", line.ToString());
    }

    [Fact]
    public void AvrAsmLine_ToString_Comment()
    {
        var line = AvrAsmLine.MakeComment("Generated by pymcuc");
        Assert.Equal("; Generated by pymcuc", line.ToString());
    }

    [Fact]
    public void AvrAsmLine_ToString_Empty_IsEmptyString()
    {
        var line = AvrAsmLine.MakeEmpty();
        Assert.Equal("", line.ToString());
    }
}
