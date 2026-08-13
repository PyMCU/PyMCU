using PyMCU.Backend.Targets.RiscV;
using Xunit;

namespace PyMCU.UnitTests;

public class RISCVPeepholeTests
{
    private static List<string> Optimize(params RISCVAsmLine[] lines)
        => RiscvPeephole.Optimize(lines.ToList()).Select(l => l.ToString()).ToList();

    private static RISCVAsmLine Ins(string m, string o1 = "", string o2 = "", string o3 = "")
        => RISCVAsmLine.MakeInstruction(m, o1, o2, o3);

    // ─── Frame-slot forwarding ───────────────────────────────────────────────

    [Fact]
    public void LoadRightAfterStoringTheSameSlotIsDropped()
    {
        var result = Optimize(
            Ins("sw", "t0", "-12(s0)"),
            Ins("lw", "t0", "-12(s0)"),
            Ins("addi", "t0", "t0", "1"));

        Assert.Equal(["\tsw\tt0, -12(s0)", "\taddi\tt0, t0, 1"], result);
    }

    [Fact]
    public void ForwardingIntoAnotherRegisterBecomesAMove()
    {
        var result = Optimize(
            Ins("sw", "t0", "-16(s0)"),
            Ins("lw", "t1", "-16(s0)"));

        Assert.Equal(["\tsw\tt0, -16(s0)", "\tmv\tt1, t0"], result);
    }

    [Fact]
    public void CommentsBetweenTheStoreAndLoadDoNotBlockForwarding()
    {
        var result = Optimize(
            Ins("sw", "t0", "-12(s0)"),
            RISCVAsmLine.MakeComment("main.py:7: x = x"),
            Ins("lw", "t0", "-12(s0)"));

        Assert.DoesNotContain("\tlw\tt0, -12(s0)", result);
    }

    [Fact]
    public void MmioStoreFollowedByLoadIsPreserved()
    {
        // Reading a peripheral register back is NOT the value just written:
        // status bits clear on write, FIFOs advance. Only s0-relative frame
        // slots may be forwarded.
        var result = Optimize(
            Ins("li", "t2", "0x40011408"),
            Ins("sw", "t0", "0(t2)"),
            Ins("lw", "t0", "0(t2)"));

        Assert.Contains("\tsw\tt0, 0(t2)", result);
        Assert.Contains("\tlw\tt0, 0(t2)", result);
    }

    [Fact]
    public void ALabelBetweenThemBlocksForwarding()
    {
        // Control can reach the load without the store having run.
        var result = Optimize(
            Ins("sw", "t0", "-12(s0)"),
            RISCVAsmLine.MakeLabel("L_1"),
            Ins("lw", "t0", "-12(s0)"));

        Assert.Contains("\tlw\tt0, -12(s0)", result);
    }

    [Fact]
    public void ADifferentSlotIsNotForwarded()
    {
        var result = Optimize(
            Ins("sw", "t0", "-12(s0)"),
            Ins("lw", "t0", "-16(s0)"));

        Assert.Contains("\tlw\tt0, -16(s0)", result);
    }

    // ─── Redundant immediates ────────────────────────────────────────────────

    [Fact]
    public void ReloadingTheSameImmediateIsDropped()
    {
        var result = Optimize(
            Ins("li", "t2", "0x40011408"),
            Ins("sw", "t0", "0(t2)"),
            Ins("li", "t2", "0x40011408"),
            Ins("sw", "t1", "0(t2)"));

        Assert.Single(result.Where(l => l.Contains("li\tt2")));
    }

    [Fact]
    public void AnImmediateIsReloadedAfterTheRegisterIsClobbered()
    {
        var result = Optimize(
            Ins("li", "t2", "100"),
            Ins("la", "t2", "counter"),
            Ins("li", "t2", "100"));

        Assert.Equal(2, result.Count(l => l.Contains("li\tt2, 100")));
    }

    [Fact]
    public void AnImmediateIsReloadedAfterALabel()
    {
        var result = Optimize(
            Ins("li", "t0", "5"),
            RISCVAsmLine.MakeLabel("L_0"),
            Ins("li", "t0", "5"));

        Assert.Equal(2, result.Count(l => l.Contains("li\tt0, 5")));
    }

    [Fact]
    public void AnImmediateIsReloadedAfterACall()
    {
        // a0 holds the callee's return value, not what we put there.
        var result = Optimize(
            Ins("li", "a0", "1"),
            Ins("call", "helper"),
            Ins("li", "a0", "1"));

        Assert.Equal(2, result.Count(l => l.Contains("li\ta0, 1")));
    }

    // ─── Self moves ──────────────────────────────────────────────────────────

    [Fact]
    public void MoveOntoItselfIsDropped()
    {
        var result = Optimize(Ins("mv", "t0", "t0"), Ins("ret"));
        Assert.Equal(["\tret"], result);
    }

    // ─── Non-instructions survive ────────────────────────────────────────────

    [Fact]
    public void DirectivesAndLabelsArePreserved()
    {
        var result = Optimize(
            RISCVAsmLine.MakeRaw(".section .text"),
            RISCVAsmLine.MakeLabel("main"),
            Ins("ret"));

        Assert.Equal([".section .text", "main:", "\tret"], result);
    }
}
