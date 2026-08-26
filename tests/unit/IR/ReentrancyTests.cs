using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// An outlined function is not reentrant: its parameters and temporaries are statically
/// allocated names, one set per subroutine rather than one per call. Two contexts that can
/// interrupt each other therefore cannot share one, and the compiler refuses rather than
/// silently letting the second entry overwrite the first one's state.
///
/// The table below is the measured one from PyMCU#125, and the halves matter equally. Refusing
/// the two hazardous shapes is the point; ACCEPTING the four safe ones is what stops the check
/// from being a tax on every program with an interrupt in it.
/// </summary>
public class ReentrancyTests
{
    private static ProgramIR Gen(string src)
    {
        var tokens = new Lexer(src).Tokenize();
        var ast = new Parser(tokens).ParseProgram();
        return new IRGenerator().Generate(ast, new Dictionary<string, ProgramNode>(),
                                          new DeviceConfig { Arch = "avr" });
    }

    private static string Refusal(string src)
        => Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(src)).Message;

    private const string Preamble =
        "from pymcu.types import uint8, uint16, interrupt, inline, asm, ptr\n" +
        "SREG: ptr[uint8] = ptr(0x5F)\n" +
        "OUT: ptr[uint8] = ptr(0x3E)\n\n";

    private const string Shared =
        "class W:\n" +
        "    def __init__(self, a: uint8, b: uint8):\n" +
        "        self.a: uint8 = a\n" +
        "        self.b: uint8 = b\n" +
        "    def work(self, k: uint8) -> uint8:\n" +
        "        return self.a + self.b + k\n";

    // ── the two shapes that are hazardous ────────────────────────────────────

    [Fact]
    public void AMethodSharedBetweenMainAndAnIsr_IsRefused()
    {
        var msg = Refusal(Preamble + Shared +
            "@interrupt(0x0020)\n" +
            "def isr():\n" +
            "    z = W(90, 90)\n" +
            "    OUT[0] = z.work(1)\n" +
            "def main():\n" +
            "    o = W(2, 3)\n" +
            "    OUT[0] = o.work(5)\n");

        Assert.Contains("W_work", msg);
    }

    [Fact]
    public void TheRefusalNamesBothContexts()
    {
        // "this function is not reentrant" is not actionable. Which two callers is.
        var msg = Refusal(Preamble + Shared +
            "@interrupt(0x0020)\n" +
            "def isr():\n" +
            "    z = W(90, 90)\n" +
            "    OUT[0] = z.work(1)\n" +
            "def main():\n" +
            "    o = W(2, 3)\n" +
            "    OUT[0] = o.work(5)\n");

        Assert.Contains("'main'", msg);
        Assert.Contains("'isr'", msg);
        Assert.Contains("@inline", msg);
    }

    [Fact]
    public void TwoIsrsAreRefusedWhenOneReenablesInterruptsWithAsm()
    {
        // main never calls it at all: "reachable from an ISR and from main" is the wrong
        // condition, and a pass built around that pairing would miss this entirely.
        var msg = Refusal(Preamble + Shared +
            "@interrupt(0x0012)\n" +
            "def slow_isr():\n" +
            "    asm(\"SEI\")\n" +
            "    s = W(90, 90)\n" +
            "    OUT[0] = s.work(1)\n" +
            "@interrupt(0x0020)\n" +
            "def fast_isr():\n" +
            "    f = W(1, 2)\n" +
            "    OUT[0] = f.work(3)\n" +
            "def main():\n" +
            "    OUT[0] = 0\n");

        Assert.Contains("slow_isr", msg);
        Assert.Contains("fast_isr", msg);
        Assert.Contains("nest", msg);
    }

    [Fact]
    public void TwoIsrsAreRefusedWhenOneSetsTheGlobalEnableBitDirectly()
    {
        // The gap I measured on the first version: the stdlib re-enables interrupts as
        // `SREG[7] = 1`, which is a bit-set on a register and not an asm node at all, so an
        // asm-only scan called this program clean.
        var msg = Refusal(Preamble + Shared +
            "@interrupt(0x0012)\n" +
            "def slow_isr():\n" +
            "    SREG[7] = 1\n" +
            "    s = W(90, 90)\n" +
            "    OUT[0] = s.work(1)\n" +
            "@interrupt(0x0020)\n" +
            "def fast_isr():\n" +
            "    f = W(1, 2)\n" +
            "    OUT[0] = f.work(3)\n" +
            "def main():\n" +
            "    OUT[0] = 0\n");

        Assert.Contains("slow_isr", msg);
        Assert.Contains("fast_isr", msg);
    }

    [Fact]
    public void APlainFunctionSharedWithAnIsr_IsRefusedToo()
    {
        // Not only methods. This is the shape the AVR corpus already contained.
        var msg = Refusal(Preamble +
            "def split_lo(v: uint16) -> uint8:\n" +
            "    return v & 0xFF\n" +
            "@interrupt(0x0020)\n" +
            "def isr():\n" +
            "    OUT[0] = split_lo(0x0101)\n" +
            "def main():\n" +
            "    OUT[0] = split_lo(0xBEEF)\n");

        Assert.Contains("split_lo", msg);
    }

    // ── the four shapes that must keep compiling ─────────────────────────────

    [Fact]
    public void TwoIsrsThatCannotPreemptEachOther_AreAccepted()
    {
        // The hardware clears the global enable on entry, so without one of them turning it
        // back on a second interrupt simply waits. Refusing this would be a tax on every
        // program with two ISRs in it.
        var ir = Gen(Preamble + Shared +
            "@interrupt(0x0012)\n" +
            "def slow_isr():\n" +
            "    s = W(90, 90)\n" +
            "    OUT[0] = s.work(1)\n" +
            "@interrupt(0x0020)\n" +
            "def fast_isr():\n" +
            "    f = W(1, 2)\n" +
            "    OUT[0] = f.work(3)\n" +
            "def main():\n" +
            "    OUT[0] = 0\n");

        Assert.Contains(ir.Functions, f => f.Name == "W_work");
    }

    [Fact]
    public void AnInlineMethodIsNeverFlagged()
    {
        // There is no shared body to re-enter, which is why @inline was the workaround. The
        // check runs on the finished IR, so an expanded body is not a function here at all.
        var ir = Gen(Preamble +
            "class W:\n" +
            "    def __init__(self, a: uint8, b: uint8):\n" +
            "        self.a: uint8 = a\n" +
            "        self.b: uint8 = b\n" +
            "    @inline\n" +
            "    def work(self, k: uint8) -> uint8:\n" +
            "        return self.a + self.b + k\n" +
            "@interrupt(0x0020)\n" +
            "def isr():\n" +
            "    z = W(90, 90)\n" +
            "    OUT[0] = z.work(1)\n" +
            "def main():\n" +
            "    asm(\"SEI\")\n" +
            "    o = W(2, 3)\n" +
            "    OUT[0] = o.work(5)\n");

        Assert.DoesNotContain(ir.Functions, f => f.Name == "W_work");
    }

    [Fact]
    public void AFunctionOnlyTheIsrCalls_IsAccepted()
    {
        var ir = Gen(Preamble + Shared +
            "@interrupt(0x0020)\n" +
            "def isr():\n" +
            "    z = W(90, 90)\n" +
            "    OUT[0] = z.work(1)\n" +
            "def main():\n" +
            "    OUT[0] = 0\n");

        Assert.Contains(ir.Functions, f => f.Name == "W_work");
    }

    [Fact]
    public void AProgramWithNoInterruptAtAll_IsAccepted()
    {
        // The cheapest exit: nothing can preempt anything, so the whole pass is skipped.
        var ir = Gen(Preamble + Shared +
            "def main():\n" +
            "    o = W(2, 3)\n" +
            "    OUT[0] = o.work(5)\n");

        Assert.Contains(ir.Functions, f => f.Name == "W_work");
    }

    [Fact]
    public void MainEnablingInterruptsDoesNotMakeIsrsNestWithEachOther()
    {
        // `asm("SEI")` in MAIN is how every interrupt program starts. It says nothing about
        // whether one ISR can enter another, and reading it as nesting would refuse the whole
        // corpus.
        var ir = Gen(Preamble + Shared +
            "@interrupt(0x0012)\n" +
            "def slow_isr():\n" +
            "    s = W(90, 90)\n" +
            "    OUT[0] = s.work(1)\n" +
            "@interrupt(0x0020)\n" +
            "def fast_isr():\n" +
            "    f = W(1, 2)\n" +
            "    OUT[0] = f.work(3)\n" +
            "def main():\n" +
            "    asm(\"SEI\")\n" +
            "    OUT[0] = 0\n");

        Assert.Contains(ir.Functions, f => f.Name == "W_work");
    }

    // ── reachability is transitive ───────────────────────────────────────────

    [Fact]
    public void AFunctionSharedThroughAnIntermediateIsFound()
    {
        var msg = Refusal(Preamble +
            "def leaf(v: uint8) -> uint8:\n" +
            "    return v + 1\n" +
            "def middle(v: uint8) -> uint8:\n" +
            "    return leaf(v) + 2\n" +
            "@interrupt(0x0020)\n" +
            "def isr():\n" +
            "    OUT[0] = middle(1)\n" +
            "def main():\n" +
            "    OUT[0] = leaf(9)\n");

        Assert.Contains("leaf", msg);
    }

    [Fact]
    public void ReenablingInterruptsInsideACalleeCounts()
    {
        // The SEI does not have to be written in the ISR's own body.
        var msg = Refusal(Preamble + Shared +
            "def open_the_door():\n" +
            "    SREG[7] = 1\n" +
            "@interrupt(0x0012)\n" +
            "def slow_isr():\n" +
            "    open_the_door()\n" +
            "    s = W(90, 90)\n" +
            "    OUT[0] = s.work(1)\n" +
            "@interrupt(0x0020)\n" +
            "def fast_isr():\n" +
            "    f = W(1, 2)\n" +
            "    OUT[0] = f.work(3)\n" +
            "def main():\n" +
            "    OUT[0] = 0\n");

        Assert.Contains("W_work", msg);
    }
}
