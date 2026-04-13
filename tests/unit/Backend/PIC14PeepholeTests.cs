using PyMCU.Backend.Targets.PIC14;
using Xunit;

namespace PyMCU.UnitTests;

public class PIC14PeepholeTests
{
    // ─── RedundantMovf ────────────────────────────────────────────────────────

    [Fact]
    public void RedundantMovf()
    {
        var lines = new List<PIC14AsmLine>
        {
            PIC14AsmLine.MakeInstruction("MOVWF", "0x20"),
            PIC14AsmLine.MakeInstruction("MOVF",  "0x20", "W")
        };
        var opt = PIC14Peephole.Optimize(lines);
        Assert.Single(opt);
        Assert.Equal("MOVWF", opt[0].Mnemonic);
    }

    // ─── RedundantMovlw ───────────────────────────────────────────────────────

    [Fact]
    public void RedundantMovlw()
    {
        var lines = new List<PIC14AsmLine>
        {
            PIC14AsmLine.MakeInstruction("MOVLW", "0x00"),
            PIC14AsmLine.MakeInstruction("MOVWF", "0x20"),
            PIC14AsmLine.MakeInstruction("MOVLW", "0x00"),
            PIC14AsmLine.MakeInstruction("MOVWF", "0x21")
        };
        var opt = PIC14Peephole.Optimize(lines);
        Assert.Equal(3, opt.Count);
        Assert.Equal("MOVLW", opt[0].Mnemonic);
        Assert.Equal("MOVWF", opt[1].Mnemonic);
        Assert.Equal("MOVWF", opt[2].Mnemonic);
    }

    // ─── RedundantBankSwitch ──────────────────────────────────────────────────

    [Fact]
    public void RedundantBankSwitch()
    {
        var lines = new List<PIC14AsmLine>
        {
            PIC14AsmLine.MakeInstruction("BSF",   "STATUS", "5"),
            PIC14AsmLine.MakeInstruction("MOVLW", "0xFF"),
            PIC14AsmLine.MakeInstruction("BSF",   "STATUS", "5")
        };
        var opt = PIC14Peephole.Optimize(lines);
        Assert.Equal(2, opt.Count);
        Assert.Equal("BSF",   opt[0].Mnemonic);
        Assert.Equal("MOVLW", opt[1].Mnemonic);
    }

    // ─── RedundantIorlw ───────────────────────────────────────────────────────

    [Fact]
    public void RedundantIorlw()
    {
        var lines = new List<PIC14AsmLine>
        {
            PIC14AsmLine.MakeInstruction("MOVF",  "0x20", "W"),
            PIC14AsmLine.MakeInstruction("IORLW", "0")
        };
        var opt = PIC14Peephole.Optimize(lines);
        Assert.Single(opt);
        Assert.Equal("MOVF", opt[0].Mnemonic);
    }

    // ─── GotoNextLabel ────────────────────────────────────────────────────────

    [Fact]
    public void GotoNextLabel()
    {
        var lines = new List<PIC14AsmLine>
        {
            PIC14AsmLine.MakeInstruction("GOTO", "L1"),
            PIC14AsmLine.MakeComment("Some comment"),
            PIC14AsmLine.MakeLabel("L1")
        };
        var opt = PIC14Peephole.Optimize(lines);
        Assert.Equal(2, opt.Count);
        Assert.Equal(PIC14AsmLine.LineType.Comment, opt[0].Type);
        Assert.Equal(PIC14AsmLine.LineType.Label,   opt[1].Type);
    }

    // ─── ComparisonToJump ─────────────────────────────────────────────────────

    [Fact]
    public void ComparisonToJump()
    {
        var lines = new List<PIC14AsmLine>
        {
            PIC14AsmLine.MakeInstruction("CLRF",  "tmp.5"),
            PIC14AsmLine.MakeInstruction("BTFSC", "STATUS", "0"),
            PIC14AsmLine.MakeInstruction("INCF",  "tmp.5", "F"),
            PIC14AsmLine.MakeInstruction("MOVF",  "tmp.5", "W"),
            PIC14AsmLine.MakeInstruction("BTFSC", "STATUS", "2"),
            PIC14AsmLine.MakeInstruction("GOTO",  "L8")
        };
        var opt = PIC14Peephole.Optimize(lines);
        // Should become: BTFSS STATUS, 0 / GOTO L8
        Assert.Equal(2, opt.Count);
        Assert.Equal("BTFSS",  opt[0].Mnemonic);
        Assert.Equal("STATUS", opt[0].Op1);
        Assert.Equal("0",      opt[0].Op2);
        Assert.Equal("GOTO",   opt[1].Mnemonic);
        Assert.Equal("L8",     opt[1].Op1);
    }

    // ─── ComparisonToJumpWithIorlw ────────────────────────────────────────────

    [Fact]
    public void ComparisonToJumpWithIorlw()
    {
        var lines = new List<PIC14AsmLine>
        {
            PIC14AsmLine.MakeInstruction("CLRF",  "tmp.1"),
            PIC14AsmLine.MakeInstruction("BTFSS", "STATUS", "0"),
            PIC14AsmLine.MakeInstruction("INCF",  "tmp.1", "F"),
            PIC14AsmLine.MakeInstruction("MOVF",  "tmp.1", "W"),
            PIC14AsmLine.MakeInstruction("IORLW", "0"),
            PIC14AsmLine.MakeInstruction("BTFSC", "STATUS", "2"),
            PIC14AsmLine.MakeInstruction("GOTO",  "L3")
        };
        var opt = PIC14Peephole.Optimize(lines);
        Assert.Equal(2, opt.Count);
        Assert.Equal("BTFSC",  opt[0].Mnemonic);
        Assert.Equal("STATUS", opt[0].Op1);
        Assert.Equal("0",      opt[0].Op2);
        Assert.Equal("GOTO",   opt[1].Mnemonic);
    }

    // ─── ComparisonToJumpNotZero ──────────────────────────────────────────────

    [Fact]
    public void ComparisonToJumpNotZero()
    {
        var lines = new List<PIC14AsmLine>
        {
            PIC14AsmLine.MakeInstruction("CLRF",  "tmp.1"),
            PIC14AsmLine.MakeInstruction("BTFSC", "STATUS", "0"),
            PIC14AsmLine.MakeInstruction("INCF",  "tmp.1", "F"),
            PIC14AsmLine.MakeInstruction("MOVF",  "tmp.1", "W"),
            PIC14AsmLine.MakeInstruction("BTFSS", "STATUS", "2"),
            PIC14AsmLine.MakeInstruction("GOTO",  "L3")
        };
        var opt = PIC14Peephole.Optimize(lines);
        Assert.Equal(2, opt.Count);
        Assert.Equal("BTFSC",  opt[0].Mnemonic);
        Assert.Equal("STATUS", opt[0].Op1);
        Assert.Equal("0",      opt[0].Op2);
    }

    // ─── RedundantStore ───────────────────────────────────────────────────────

    [Fact]
    public void RedundantStore()
    {
        var lines = new List<PIC14AsmLine>
        {
            PIC14AsmLine.MakeInstruction("MOVF",  "0x20", "W"),
            PIC14AsmLine.MakeInstruction("MOVWF", "0x20")
        };
        var opt = PIC14Peephole.Optimize(lines);
        Assert.Single(opt);
        Assert.Equal("MOVF", opt[0].Mnemonic);
    }

    // ─── DeadCodeAfterReturn ──────────────────────────────────────────────────

    [Fact]
    public void DeadCodeAfterReturn()
    {
        var lines = new List<PIC14AsmLine>
        {
            PIC14AsmLine.MakeInstruction("RETURN"),
            PIC14AsmLine.MakeInstruction("MOVLW", "0x00"),
            PIC14AsmLine.MakeLabel("L1")
        };
        var opt = PIC14Peephole.Optimize(lines);
        Assert.Equal(2, opt.Count);
        Assert.Equal("RETURN",              opt[0].Mnemonic);
        Assert.Equal(PIC14AsmLine.LineType.Label, opt[1].Type);
    }

    // ─── MathIdentities ───────────────────────────────────────────────────────

    [Fact]
    public void MathIdentities()
    {
        var lines = new List<PIC14AsmLine>
        {
            PIC14AsmLine.MakeInstruction("MOVF",  "0x20", "W"),
            PIC14AsmLine.MakeInstruction("ADDLW", "0"),
            PIC14AsmLine.MakeInstruction("IORLW", "0"),
            PIC14AsmLine.MakeInstruction("XORLW", "0"),
            PIC14AsmLine.MakeInstruction("ANDLW", "0xFF"),
            PIC14AsmLine.MakeInstruction("MOVLW", "0x01")
        };
        var opt = PIC14Peephole.Optimize(lines);
        Assert.Equal(2, opt.Count);
        Assert.Equal("MOVF",  opt[0].Mnemonic);
        Assert.Equal("MOVLW", opt[1].Mnemonic);
    }

    // ─── BitCoalescingRedundant ───────────────────────────────────────────────

    [Fact]
    public void BitCoalescingRedundant()
    {
        var lines = new List<PIC14AsmLine>
        {
            PIC14AsmLine.MakeInstruction("BCF", "0x20", "1"),
            PIC14AsmLine.MakeInstruction("BSF", "0x20", "1")
        };
        var opt = PIC14Peephole.Optimize(lines);
        Assert.Single(opt);
        Assert.Equal("BSF", opt[0].Mnemonic);
    }

    // ─── JumpToNextWithSkip ───────────────────────────────────────────────────

    [Fact]
    public void JumpToNextWithSkip()
    {
        var lines = new List<PIC14AsmLine>
        {
            PIC14AsmLine.MakeInstruction("BTFSC", "STATUS", "2"),
            PIC14AsmLine.MakeInstruction("GOTO",  "L1"),
            PIC14AsmLine.MakeLabel("L1")
        };
        var opt = PIC14Peephole.Optimize(lines);
        Assert.Single(opt);
        Assert.Equal(PIC14AsmLine.LineType.Label, opt[0].Type);
    }

    // ─── GotoNextLabelMultiple ────────────────────────────────────────────────

    [Fact]
    public void GotoNextLabelMultiple()
    {
        var lines = new List<PIC14AsmLine>
        {
            PIC14AsmLine.MakeInstruction("GOTO", "L11"),
            PIC14AsmLine.MakeLabel("L10"),
            PIC14AsmLine.MakeLabel("L11")
        };
        var opt = PIC14Peephole.Optimize(lines);
        Assert.Equal(2, opt.Count);
        Assert.Equal("L10", opt[0].LabelText);
        Assert.Equal("L11", opt[1].LabelText);
    }

    // ─── DeadLabelElimination ─────────────────────────────────────────────────

    [Fact]
    public void DeadLabelElimination()
    {
        var lines = new List<PIC14AsmLine>
        {
            PIC14AsmLine.MakeInstruction("GOTO",   "L.1"),
            PIC14AsmLine.MakeLabel("L.2"),           // dead label
            PIC14AsmLine.MakeLabel("L.1"),
            PIC14AsmLine.MakeInstruction("RETURN"),
            PIC14AsmLine.MakeLabel("ExternalEntryPoint"), // not internal
            PIC14AsmLine.MakeInstruction("GOTO",   "L.1") // keeps L.1 alive
        };
        var opt = PIC14Peephole.Optimize(lines);
        // L.2 removed; first GOTO L.1 removed (it's the next label after removing L.2)
        // → L.1, RETURN, ExternalEntryPoint, GOTO L.1
        Assert.Equal(4, opt.Count);
        Assert.Equal("L.1",                opt[0].LabelText);
        Assert.Equal("RETURN",             opt[1].Mnemonic);
        Assert.Equal("ExternalEntryPoint", opt[2].LabelText);
        Assert.Equal("GOTO",               opt[3].Mnemonic);
    }

    // ─── SkipDoesNotAllowDeadCodeRemoval ─────────────────────────────────────

    [Fact]
    public void SkipDoesNotAllowDeadCodeRemoval()
    {
        var lines = new List<PIC14AsmLine>
        {
            PIC14AsmLine.MakeInstruction("BTFSC", "STATUS", "2"),
            PIC14AsmLine.MakeInstruction("RETURN"),
            PIC14AsmLine.MakeInstruction("MOVLW", "0x01"), // must NOT be removed
            PIC14AsmLine.MakeLabel("L1")
        };
        var opt = PIC14Peephole.Optimize(lines);
        Assert.Equal(4, opt.Count);
        Assert.Equal("MOVLW", opt[2].Mnemonic);
    }

    // ─── BitCoalescingMultipleBits ────────────────────────────────────────────

    [Fact]
    public void BitCoalescingMultipleBits()
    {
        var lines = new List<PIC14AsmLine>
        {
            PIC14AsmLine.MakeInstruction("BSF", "0x85", "0"),
            PIC14AsmLine.MakeInstruction("BSF", "0x85", "1"),
            PIC14AsmLine.MakeInstruction("BSF", "0x85", "2"),
            PIC14AsmLine.MakeInstruction("BSF", "0x85", "3")
        };
        var opt = PIC14Peephole.Optimize(lines);
        // MOVLW 0x0F / IORWF 0x85, F
        Assert.Equal(2, opt.Count);
        Assert.Equal("MOVLW", opt[0].Mnemonic);
        Assert.Equal("0x0F",  opt[0].Op1);
        Assert.Equal("IORWF", opt[1].Mnemonic);
        Assert.Equal("0x85",  opt[1].Op1);
        Assert.Equal("F",     opt[1].Op2);
    }

    // ─── BCFCoalescingMultipleBits ────────────────────────────────────────────

    [Fact]
    public void BCFCoalescingMultipleBits()
    {
        var lines = new List<PIC14AsmLine>
        {
            PIC14AsmLine.MakeInstruction("BCF", "0x85", "0"),
            PIC14AsmLine.MakeInstruction("BCF", "0x85", "1"),
            PIC14AsmLine.MakeInstruction("BCF", "0x85", "2")
        };
        var opt = PIC14Peephole.Optimize(lines);
        // MOVLW 0xF8 / ANDWF 0x85, F
        Assert.Equal(2, opt.Count);
        Assert.Equal("MOVLW", opt[0].Mnemonic);
        Assert.Equal("0xF8",  opt[0].Op1);
        Assert.Equal("ANDWF", opt[1].Mnemonic);
        Assert.Equal("0x85",  opt[1].Op1);
        Assert.Equal("F",     opt[1].Op2);
    }

    // ─── CopyPropagation ─────────────────────────────────────────────────────

    [Fact]
    public void CopyPropagation()
    {
        var lines = new List<PIC14AsmLine>
        {
            PIC14AsmLine.MakeInstruction("MOVWF", "tmp.1"),
            PIC14AsmLine.MakeInstruction("MOVF",  "tmp.1", "W"),
            PIC14AsmLine.MakeInstruction("MOVWF", "0x20")
        };
        var opt = PIC14Peephole.Optimize(lines);
        // Result: MOVWF 0x20
        Assert.Single(opt);
        Assert.Equal("MOVWF", opt[0].Mnemonic);
        Assert.Equal("0x20",  opt[0].Op1);
    }

    // ─── IncrementOptimization ───────────────────────────────────────────────

    [Fact]
    public void IncrementOptimization()
    {
        var lines = new List<PIC14AsmLine>
        {
            PIC14AsmLine.MakeInstruction("MOVF",  "0x20", "W"),
            PIC14AsmLine.MakeInstruction("ADDLW", "1"),
            PIC14AsmLine.MakeInstruction("MOVWF", "tmp.2"),
            PIC14AsmLine.MakeInstruction("MOVF",  "tmp.2", "W"),
            PIC14AsmLine.MakeInstruction("MOVWF", "0x20")
        };
        var opt = PIC14Peephole.Optimize(lines);
        // Result: INCF 0x20, F
        Assert.Single(opt);
        Assert.Equal("INCF", opt[0].Mnemonic);
        Assert.Equal("0x20", opt[0].Op1);
        Assert.Equal("F",    opt[0].Op2);
    }
}
