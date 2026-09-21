using FluentAssertions;
using PyMCU.Common;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// all()/any()/sum()/min()/max() over a generator expression unroll it at compile time.
/// These pin what the unroll must do: fold constant elements (the pixelbuf
/// `all(0 <= component <= 255 for component in val)` reading), keep CPython's
/// short-circuit for all/any (an element that decides the answer ends the walk, so a
/// later element is never evaluated), answer the accumulations sum/min/max fold to, and
/// refuse an iterable whose length is not compile-time known -- with the same message
/// from both front ends.
/// </summary>
public class GenExpReductionTests
{
    private const string Regs =
        "from pymcu.chips.atmega328p import GPIOR0, GPIOR1\n" +
        "from pymcu.types import uint8, int32, inline\n\n";

    private static ProgramIR Gen(string src, bool pyParser = false)
    {
        var ast = pyParser
            ? PythonAstReader.ParseSource(src, "main.py")
            : new Parser(new Lexer(src).Tokenize()).ParseProgram();
        return new IRGenerator().Generate(ast, new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });
    }

    private static string GenError(string src, bool pyParser = false)
    {
        var ex = Assert.ThrowsAny<CompilerError>(() => Gen(src, pyParser));
        return ex.Message;
    }

    private static List<Instruction> Main(ProgramIR ir) =>
        ir.Functions.Single(f => f.Name == "main").Body;

    private static bool WritesGpio(List<Instruction> body, string reg, int value) =>
        body.Any(i => i is Copy { Src: Constant { Value: var v }, Dst: Variable { Name: var n } }
                      && v == value && n.EndsWith(reg, StringComparison.Ordinal));

    // ── all(): the pixelbuf idiom ───────────────────────────────────────────────

    [Fact]
    public void AllOverATupleFoldsToTrue()
    {
        // adafruit_pixelbuf.py:299: all(0 <= component <= 255 for component in val).
        var ir = Gen(
            Regs +
            "def main():\n" +
            "    val = (1, 2, 3)\n" +
            "    if all(0 <= c <= 255 for c in val):\n" +
            "        GPIOR0.value = 7\n");
        WritesGpio(Main(ir), "GPIOR0", 7).Should().BeTrue(
            "every element satisfies the predicate, so all() is true");
    }

    [Fact]
    public void AllOverATupleFoldsToFalse()
    {
        var ir = Gen(
            Regs +
            "def main():\n" +
            "    val = (1, 2, 300)\n" +
            "    if all(0 <= c <= 255 for c in val):\n" +
            "        GPIOR0.value = 7\n" +
            "    else:\n" +
            "        GPIOR0.value = 3\n");
        WritesGpio(Main(ir), "GPIOR0", 7).Should().BeFalse();
        WritesGpio(Main(ir), "GPIOR0", 3).Should().BeTrue("300 fails the predicate");
    }

    [Fact]
    public void AllShortCircuitsBeforeEvaluatingALaterElement()
    {
        // The 0 decides all() at compile time; the trailing missing() is an element the
        // walk must never reach -- if it did, the undefined call would refuse the build.
        // The tuple is inline in the genexp so its elements bind lazily, per iteration.
        var ir = Gen(
            Regs +
            "def main():\n" +
            "    if all(x > 0 for x in (4, 0, missing())):\n" +
            "        GPIOR0.value = 9\n" +
            "    else:\n" +
            "        GPIOR0.value = 2\n");
        WritesGpio(Main(ir), "GPIOR0", 2).Should().BeTrue(
            "all() saw 0 and stopped; missing() was never evaluated");
    }

    [Fact]
    public void AnyShortCircuitsBeforeEvaluatingALaterElement()
    {
        var ir = Gen(
            Regs +
            "def main():\n" +
            "    if any(x > 0 for x in (0, 5, missing())):\n" +
            "        GPIOR0.value = 9\n" +
            "    else:\n" +
            "        GPIOR0.value = 2\n");
        WritesGpio(Main(ir), "GPIOR0", 9).Should().BeTrue(
            "any() saw 5 and stopped; missing() was never evaluated");
    }

    [Fact]
    public void AllWithRuntimeElementsEmitsTheShortCircuitJump()
    {
        // Elements of a fixed-size array are runtime reads: each one must produce a
        // conditional exit, so a false element answers without visiting the rest.
        var ir = Gen(
            Regs +
            "def main():\n" +
            "    val: uint8[3] = [1, 2, 3]\n" +
            "    if all(x > 0 for x in val):\n" +
            "        GPIOR0.value = 9\n");
        Main(ir).OfType<JumpIfZero>().Count().Should().BeGreaterThanOrEqualTo(3,
            "each runtime element gets its own early-exit test");
    }

    // ── sum / min / max ─────────────────────────────────────────────────────────

    [Fact]
    public void SumOverATupleFolds()
    {
        var ir = Gen(
            Regs +
            "def main():\n" +
            "    GPIOR0.value = sum(x for x in (1, 2, 3))\n");
        WritesGpio(Main(ir), "GPIOR0", 6).Should().BeTrue();
    }

    [Fact]
    public void SumHonoursTheStartArgument()
    {
        var ir = Gen(
            Regs +
            "def main():\n" +
            "    GPIOR0.value = sum((x for x in (1, 2, 3)), 10)\n");
        WritesGpio(Main(ir), "GPIOR0", 16).Should().BeTrue();
    }

    [Fact]
    public void MinAndMaxFoldOverConstants()
    {
        var ir = Gen(
            Regs +
            "def main():\n" +
            "    GPIOR0.value = min(x for x in (4, 2, 8))\n" +
            "    GPIOR1.value = max(x for x in (4, 2, 8))\n");
        WritesGpio(Main(ir), "GPIOR0", 2).Should().BeTrue();
        WritesGpio(Main(ir), "GPIOR1", 8).Should().BeTrue();
    }

    // ── iterables beyond literals ───────────────────────────────────────────────

    [Fact]
    public void AGeneratorOverAConstantStringIteratesItsCharacters()
    {
        var ir = Gen(
            Regs +
            "def main():\n" +
            "    if all(c != \"x\" for c in \"ab\"):\n" +
            "        GPIOR0.value = 7\n");
        WritesGpio(Main(ir), "GPIOR0", 7).Should().BeTrue();
    }

    [Fact]
    public void AGeneratorOverConstantRangeFolds()
    {
        var ir = Gen(
            Regs +
            "def main():\n" +
            "    GPIOR0.value = sum(x for x in range(4))\n");
        WritesGpio(Main(ir), "GPIOR0", 6).Should().BeTrue("0+1+2+3");
    }

    [Fact]
    public void AGeneratorFilterExcludesElements()
    {
        var ir = Gen(
            Regs +
            "def main():\n" +
            "    GPIOR0.value = sum(x for x in (1, 2, 3, 4) if x % 2 == 0)\n");
        WritesGpio(Main(ir), "GPIOR0", 6).Should().BeTrue("only the even elements: 2+4");
    }

    [Fact]
    public void AllUnderARuntimeFilterCannotFoldTheVerdict()
    {
        // `all(x > 0 for x in (0, 5) if flag)`: the 0 fails the predicate -- but only
        // when flag lets it be produced. flag == 0 makes the sequence the predicate sees
        // empty and all() is True, so the answer is a runtime value, never a folded 0.
        var ir = Gen(
            Regs +
            "def main():\n" +
            "    flag: uint8 = GPIOR1.value\n" +
            "    GPIOR0.value = all(x > 0 for x in (0, 5) if flag)\n");
        WritesGpio(Main(ir), "GPIOR0", 0).Should().BeFalse(
            "a folded 0 ignores flag == 0, where all() of the empty sequence is True");
        Main(ir).OfType<JumpIfZero>().Count().Should().BeGreaterThanOrEqualTo(1,
            "the runtime filter and the conditional exit both need a jump");
    }

    [Fact]
    public void MinUnderARuntimeFilterKeepsTheSeedMutable()
    {
        // `min(x for x in (5, 3) if x == 5 or flag)`: the filter folds true for the 5
        // (seeding the answer at compile time) but stays runtime for the 3 -- folding
        // that 3 into the seed would report it even when flag == 0 excludes it, and the
        // real answer then is 5. The seed must live in a variable the filter's skip
        // jump protects.
        var ir = Gen(
            Regs +
            "def main():\n" +
            "    flag: uint8 = GPIOR1.value\n" +
            "    GPIOR0.value = min(x for x in (5, 3) if x == 5 or flag)\n");
        WritesGpio(Main(ir), "GPIOR0", 3).Should().BeFalse(
            "a folded 3 is wrong when flag excludes the element that produces it");
        Main(ir).OfType<JumpIfZero>().Count().Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public void SumUnderARuntimeFilterAccumulatesConditionally()
    {
        // `sum(x for x in (1, 2) if flag)` is 0 when flag == 0: the fold into a constant
        // 3 would answer for a path the filter never lets run.
        var ir = Gen(
            Regs +
            "def main():\n" +
            "    flag: uint8 = GPIOR1.value\n" +
            "    GPIOR0.value = sum(x for x in (1, 2) if flag)\n");
        WritesGpio(Main(ir), "GPIOR0", 3).Should().BeFalse(
            "a folded 3 ignores flag == 0, where sum() of the empty sequence is 0");
        Main(ir).OfType<JumpIfZero>().Count().Should().BeGreaterThanOrEqualTo(1);
    }

    // ── refusals ────────────────────────────────────────────────────────────────

    [Fact]
    public void AGeneratorOverARuntimeSizedIterableIsRefused()
    {
        // `for` over a runtime sequence is a real loop, but a generator's whole meaning
        // here is compile-time unrolling -- a runtime length cannot be one.
        var msg = GenError(
            Regs +
            "def main():\n" +
            "    n: uint8 = GPIOR0.value\n" +
            "    if all(x > 0 for x in range(n)):\n" +
            "        GPIOR0.value = 9\n");
        msg.Should().Contain("all()").And.Contain("not known when the program is compiled");
    }

    [Fact]
    public void AGeneratorAsACallArgumentOtherThanAReductionIsRefused()
    {
        var msg = GenError(
            Regs +
            "@inline\n" +
            "def take(g) -> uint8:\n" +
            "    return 0\n" +
            "def main():\n" +
            "    GPIOR0.value = take(x for x in (1, 2))\n");
        msg.Should().Contain("generator expression").And.Contain("all()");
    }

    [Fact]
    public void MinOfACompletelyFilteredGeneratorIsRefused()
    {
        // CPython raises ValueError on min() of an empty sequence; when the filter excludes
        // every element at compile time the program asks for exactly that.
        var msg = GenError(
            Regs +
            "def main():\n" +
            "    GPIOR0.value = min(x for x in (1, 2) if x > 9)\n");
        msg.Should().Contain("min()").And.Contain("empty");
    }

    // ── front-end parity ────────────────────────────────────────────────────────

    [Fact]
    public void BothFrontEndsFoldAllOverATupleIdentically()
    {
        const string src =
            "def main():\n" +
            "    val = (1, 2, 3)\n" +
            "    if all(0 <= c <= 255 for c in val):\n" +
            "        GPIOR0.value = 7\n";
        var hand = Main(Gen(Regs + src));
        var py = Main(Gen(Regs + src, pyParser: true));
        WritesGpio(hand, "GPIOR0", 7).Should().BeTrue("hand-written parser folds all()");
        WritesGpio(py, "GPIOR0", 7).Should().BeTrue("CPython front end folds all()");
    }

    [Fact]
    public void BothFrontEndsRefuseARuntimeSizedGeneratorTheSameWay()
    {
        const string src =
            "def main():\n" +
            "    n: uint8 = GPIOR0.value\n" +
            "    if all(x > 0 for x in range(n)):\n" +
            "        GPIOR0.value = 9\n";
        var hand = GenError(Regs + src);
        var py = GenError(Regs + src, pyParser: true);
        hand.Should().Be(py, "the refusal is a property of the program, not the parser");
    }
}
