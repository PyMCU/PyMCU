using FluentAssertions;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// The unroll policy (ConstSequenceUnrollLimit for the trip count,
/// ConstSequenceUnrollBodyLimit for the body): a compile-time `for` copies its
/// body once per element only when the trip count is at most 8 AND the body holds
/// no nested loop, no comprehension, and no call whose inline expansion exceeds
/// the body limit. Anything else lowers to the run-time counter loop the same
/// source would have written with `while`.
///
/// The demandant was Conway's Life on the unmodified adafruit_ssd1306: a
/// `for y in range(8)` whose body is another `for` plus an inlined I2C burst
/// unrolled to eight copies of the whole thing, and a 513-element framebuffer
/// `enumerate()` unrolled into 513 element loads that never fit an Uno.
/// </summary>
public class UnrollPolicyTests
{
    private const string Regs =
        "from pymcu.chips.atmega328p import GPIOR0, GPIOR1\n" +
        "from pymcu.types import uint8, int32, inline\n\n";

    private static ProgramNode Parse(string src) =>
        new Parser(new Lexer(src).Tokenize()).ParseProgram();

    private static ProgramIR Gen(string src)
    {
        var config = new DeviceConfig { Arch = "avr", Stdlib = "" };
        var mainAst = Parse(src);
        new ConditionalCompilator(config) { ModuleName = "__main__" }.Process(mainAst);
        return new IRGenerator().Generate(mainAst, new Dictionary<string, ProgramNode>(), config);
    }

    private static List<Instruction> Main(ProgramIR ir) =>
        ir.Functions.Single(f => f.Name == "main").Body;

    // A `GPIOR0.value = ...` store -- one per unrolled copy of the body when the
    // loop unrolls, one total when it lowers to a counter loop.
    private static int GpioWrites(List<Instruction> body) =>
        body.Count(i => i is Copy { Dst: Variable { Name: var n } }
                        && n.EndsWith("GPIOR0", StringComparison.Ordinal));

    // A backward edge marks the counter loop the policy asks for instead.
    private static bool HasLoopBackEdge(List<Instruction> body)
    {
        var labels = new Dictionary<string, int>();
        for (int i = 0; i < body.Count; i++)
            if (body[i] is Label l) labels[l.Name] = i;
        return body.Select((ins, i) => (ins, i))
            .Any(t => t.ins is Jump j && labels.TryGetValue(j.Target, out int p) && p <= t.i);
    }

    // ── range() ─────────────────────────────────────────────────────────────

    [Fact]
    public void AShortCheapRangeBodyStillUnrolls()
    {
        var ir = Gen(
            Regs +
            "def main():\n" +
            "    for i in range(4):\n" +
            "        GPIOR0.value = i\n");
        GpioWrites(Main(ir)).Should().Be(4, "a cheap body at trip 4 unrolls as before");
        HasLoopBackEdge(Main(ir)).Should().BeFalse();
    }

    [Fact]
    public void ARangeBodyWithANestedForDoesNotUnroll()
    {
        // The Life shape: `for y in range(8): for x in range(32): ...`. The outer
        // loop lowers to a counter loop; the inner one, a cheap body at trip 3,
        // still unrolls inside it -- so three writes, not six.
        var ir = Gen(
            Regs +
            "def main():\n" +
            "    for y in range(2):\n" +
            "        for x in range(3):\n" +
            "            GPIOR0.value = x\n");
        GpioWrites(Main(ir)).Should().Be(3, "the outer loop is a counter, not two copies");
        HasLoopBackEdge(Main(ir)).Should().BeTrue();
    }

    [Fact]
    public void ARangeBodyWithANestedWhileDoesNotUnroll()
    {
        var ir = Gen(
            Regs +
            "def main():\n" +
            "    n = 0\n" +
            "    for i in range(2):\n" +
            "        while n < 3:\n" +
            "            GPIOR0.value = n\n" +
            "            n = n + 1\n");
        GpioWrites(Main(ir)).Should().Be(1);
        HasLoopBackEdge(Main(ir)).Should().BeTrue();
    }

    [Fact]
    public void ARangeBodyPastTheNodeLimitDoesNotUnroll()
    {
        // Each `GPIOR0.value = k` is four nodes in the count (store, member, name,
        // literal), so seven of them pass ConstSequenceUnrollBodyLimit = 24.
        var bodyLines = string.Concat(Enumerable.Range(0, 7)
            .Select(k => $"        GPIOR0.value = {k}\n"));
        var ir = Gen(Regs + "def main():\n    for i in range(2):\n" + bodyLines);
        GpioWrites(Main(ir)).Should().Be(7, "the body lowers once, not twice");
        HasLoopBackEdge(Main(ir)).Should().BeTrue();
    }

    [Fact]
    public void ARangeBodyAtTheNodeLimitStillUnrolls()
    {
        // Six assignments are exactly ConstSequenceUnrollBodyLimit nodes: the
        // boundary is inclusive.
        var bodyLines = string.Concat(Enumerable.Range(0, 6)
            .Select(k => $"        GPIOR0.value = {k}\n"));
        var ir = Gen(Regs + "def main():\n    for i in range(2):\n" + bodyLines);
        GpioWrites(Main(ir)).Should().Be(12);
        HasLoopBackEdge(Main(ir)).Should().BeFalse();
    }

    [Fact]
    public void ARangeBodyWithAnExpensiveInlineCallDoesNotUnroll()
    {
        // display.show() is the shape: an @inline whose expansion is a whole I2C
        // burst. Four copies of it is four programs, so the loop stays a counter.
        var pulseBody = string.Concat(Enumerable.Range(0, 15)
            .Select(k => $"    GPIOR{(k & 1)}.value = n\n"));
        var ir = Gen(
            Regs +
            "@inline\n" +
            "def pulse(n):\n" + pulseBody +
            "def main():\n" +
            "    for i in range(4):\n" +
            "        pulse(i)\n");
        GpioWrites(Main(ir)).Should().Be(8, "pulse expands once; half its writes are GPIOR0");
        HasLoopBackEdge(Main(ir)).Should().BeTrue();
    }

    [Fact]
    public void ARangeStarTupleHonoursTheBodyPolicy()
    {
        // `range(*t)` used to unroll unconditionally -- no trip cap, no body
        // check, no break/continue labels. Spliced into the canonical range call
        // it gets all three now.
        var bodyLines = string.Concat(Enumerable.Range(0, 25)
            .Select(k => $"        GPIOR0.value = {k}\n"));
        var ir = Gen(
            Regs +
            "def main():\n" +
            "    t = (0, 2)\n" +
            "    for i in range(*t):\n" + bodyLines);
        GpioWrites(Main(ir)).Should().Be(25);
        HasLoopBackEdge(Main(ir)).Should().BeTrue();
    }

    // ── constant sequences ──────────────────────────────────────────────────

    [Fact]
    public void AShortLiteralTupleStillUnrolls()
    {
        var ir = Gen(
            Regs +
            "def main():\n" +
            "    for v in (1, 2, 3):\n" +
            "        GPIOR0.value = v\n");
        GpioWrites(Main(ir)).Should().Be(3);
        HasLoopBackEdge(Main(ir)).Should().BeFalse();
    }

    [Fact]
    public void ALongLiteralTupleRunsACounterLoop()
    {
        // Nine compile-time integers unrolled nine copies before; the same trip
        // cap range() has reads them from a materialised flash table instead.
        var ir = Gen(
            Regs +
            "def main():\n" +
            "    for v in (10, 11, 12, 13, 14, 15, 16, 17, 18):\n" +
            "        GPIOR0.value = v\n");
        GpioWrites(Main(ir)).Should().Be(1);
        HasLoopBackEdge(Main(ir)).Should().BeTrue();
    }

    [Fact]
    public void ANamedSequencePastTheCapRunsACounterLoop()
    {
        var ir = Gen(
            Regs +
            "def main():\n" +
            "    pins = (3, 4, 5, 6, 7, 8, 9, 10, 11)\n" +
            "    for v in pins:\n" +
            "        GPIOR0.value = v\n");
        GpioWrites(Main(ir)).Should().Be(1);
        HasLoopBackEdge(Main(ir)).Should().BeTrue();
    }

    [Fact]
    public void AShortSequenceWithAHeavyBodyRunsACounterLoop()
    {
        var bodyLines = string.Concat(Enumerable.Range(0, 25)
            .Select(k => $"        GPIOR0.value = {k}\n"));
        var ir = Gen(
            Regs +
            "def main():\n" +
            "    for v in (1, 2):\n" + bodyLines);
        GpioWrites(Main(ir)).Should().Be(25);
        HasLoopBackEdge(Main(ir)).Should().BeTrue();
    }

    [Fact]
    public void APairUnpackStillUnrolls()
    {
        // `for a, b in [(1, 2), ...]` elements are pairs -- no flat table to
        // index, so the policy leaves the unroll alone.
        var ir = Gen(
            Regs +
            "def main():\n" +
            "    for a, b in [(1, 2), (3, 4)]:\n" +
            "        GPIOR0.value = a + b\n");
        HasLoopBackEdge(Main(ir)).Should().BeFalse();
    }

    // ── arrays ──────────────────────────────────────────────────────────────

    [Fact]
    public void ASmallArrayWithACheapBodyStillUnrolls()
    {
        var ir = Gen(
            Regs +
            "def main():\n" +
            "    xs: uint8[3] = [4, 5, 6]\n" +
            "    for v in xs:\n" +
            "        GPIOR0.value = v\n");
        GpioWrites(Main(ir)).Should().Be(3);
        HasLoopBackEdge(Main(ir)).Should().BeFalse();
    }

    [Fact]
    public void AWideArrayRunsACounterLoop()
    {
        var ir = Gen(
            Regs +
            "def main():\n" +
            "    xs: uint8[16] = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15]\n" +
            "    for v in xs:\n" +
            "        GPIOR0.value = v\n");
        GpioWrites(Main(ir)).Should().Be(1);
        HasLoopBackEdge(Main(ir)).Should().BeTrue();
    }

    [Fact]
    public void AConstantBoundedSlicePastTheCapRunsACounterLoop()
    {
        var ir = Gen(
            Regs +
            "def main():\n" +
            "    xs: uint8[16] = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15]\n" +
            "    for v in xs[0:12]:\n" +
            "        GPIOR0.value = v\n");
        GpioWrites(Main(ir)).Should().Be(1);
        HasLoopBackEdge(Main(ir)).Should().BeTrue();
    }

    // ── enumerate() ─────────────────────────────────────────────────────────

    [Fact]
    public void EnumerateOverAWideArrayRunsACounterLoop()
    {
        // The framebuffer burst: `for i, b in enumerate(buffer)` on a 513-byte
        // array. It used to emit 513 element loads; now it is one body and an
        // index that walks them.
        var ir = Gen(
            Regs +
            "def main():\n" +
            "    xs: uint8[16] = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15]\n" +
            "    for i, v in enumerate(xs):\n" +
            "        GPIOR0.value = v\n");
        GpioWrites(Main(ir)).Should().Be(1);
        HasLoopBackEdge(Main(ir)).Should().BeTrue();
    }

    [Fact]
    public void EnumerateOverASmallArrayStillUnrolls()
    {
        var ir = Gen(
            Regs +
            "def main():\n" +
            "    xs: uint8[3] = [4, 5, 6]\n" +
            "    for i, v in enumerate(xs):\n" +
            "        GPIOR0.value = v\n");
        GpioWrites(Main(ir)).Should().Be(3);
        HasLoopBackEdge(Main(ir)).Should().BeFalse();
    }

    [Fact]
    public void EnumerateOverASmallSequenceWithAHeavyBodyRunsACounterLoop()
    {
        var bodyLines = string.Concat(Enumerable.Range(0, 25)
            .Select(k => $"        GPIOR0.value = {k}\n"));
        var ir = Gen(
            Regs +
            "def main():\n" +
            "    for i, v in enumerate((1, 2)):\n" + bodyLines);
        GpioWrites(Main(ir)).Should().Be(25);
        HasLoopBackEdge(Main(ir)).Should().BeTrue();
    }

    // ── reversed() ──────────────────────────────────────────────────────────

    [Fact]
    public void ReversedOverAWideArrayRunsACounterLoop()
    {
        var ir = Gen(
            Regs +
            "def main():\n" +
            "    xs: uint8[16] = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15]\n" +
            "    for v in reversed(xs):\n" +
            "        GPIOR0.value = v\n");
        GpioWrites(Main(ir)).Should().Be(1);
        HasLoopBackEdge(Main(ir)).Should().BeTrue();
    }

    // ── strings ─────────────────────────────────────────────────────────────

    [Fact]
    public void AShortStringWithACheapBodyStillUnrolls()
    {
        var ir = Gen(
            Regs +
            "def main():\n" +
            "    for c in \"ab\":\n" +
            "        GPIOR0.value = 1\n");
        GpioWrites(Main(ir)).Should().Be(2);
        HasLoopBackEdge(Main(ir)).Should().BeFalse();
    }

    [Fact]
    public void AShortStringWithAHeavyBodyReadsAFlashTable()
    {
        // The body limit applies to strings too: `for c in s` over a short
        // string with a heavy body walks the interned flash bytes once.
        var bodyLines = string.Concat(Enumerable.Range(0, 25)
            .Select(k => $"        GPIOR0.value = {k}\n"));
        var ir = Gen(
            Regs +
            "def main():\n" +
            "    for c in \"ab\":\n" + bodyLines);
        GpioWrites(Main(ir)).Should().Be(25);
        HasLoopBackEdge(Main(ir)).Should().BeTrue();
    }
}
