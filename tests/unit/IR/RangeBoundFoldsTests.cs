using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU#326. The bounds of a plain `for x in range(...)` are folded before the unroller
/// decides, whatever shape they were written in. Only a literal or a NAME folded before, so a
/// bound that is an EXPRESSION -- which is what `range(total_us // 60000000)` becomes once the
/// division is done -- was lowered as a run-time counter loop over a 32-bit bound. In the
/// CircuitPython layer's sleep() that cost 1814 bytes on a 380-byte program.
///
/// An empty range now emits nothing rather than a loop that immediately exits.
/// </summary>
public class RangeBoundFoldsTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    private static Function Main(ProgramIR ir) => ir.Functions.Last(f => f.Name == "main");

    private static List<int> RegisterWrites(ProgramIR ir) =>
        Main(ir).Body.OfType<Copy>()
            .Where(c => c.Dst is Variable v && v.Name.EndsWith("GPIOR0", StringComparison.Ordinal))
            .Select(c => c.Src is Constant k ? k.Value : -1)
            .ToList();

    private static bool HasLoop(ProgramIR ir) =>
        Main(ir).Body.Any(i => i is JumpIfGreaterOrEqual or JumpIfLessOrEqual);

    private const string Prelude =
        "from pymcu.types import uint8, uint32, inline\n" +
        "from pymcu.chips.atmega328p import GPIOR0\n" +
        "\n" +
        "@inline\n" +
        "def a(seconds: float):\n" +
        "    total_us: uint32 = uint32(seconds * 1000000.0)\n" +
        "    for _ in range(total_us // 60000000):\n" +
        "        GPIOR0.value = 1\n" +
        "\n" +
        "@inline\n" +
        "def b(n: uint8):\n" +
        "    for _ in range(n // 4):\n" +
        "        GPIOR0.value = 2\n" +
        "\n";

    [Fact]
    public void ABoundThatFoldsToZero_LowersNothing()
    {
        var ir = Gen(Prelude + "def main():\n    a(1.0)\n");
        Assert.Empty(RegisterWrites(ir));
        Assert.False(HasLoop(ir), "an empty range needs no counter and no exit test");
    }

    [Fact]
    public void ABoundThatFoldsToAShortCount_Unrolls()
    {
        var ir = Gen(Prelude + "def main():\n    b(12)\n");
        Assert.Equal(new List<int> { 2, 2, 2 }, RegisterWrites(ir));
        Assert.False(HasLoop(ir));
    }

    [Fact]
    public void AnArithmeticBoundOnAModuleConstant_UnrollsToo()
    {
        var ir = Gen(
            "from pymcu.types import uint8\n" +
            "from pymcu.chips.atmega328p import GPIOR0\n" +
            "\n" +
            "WIDTH = 8\n" +
            "\n" +
            "def main():\n" +
            "    for i in range(WIDTH // 2):\n" +
            "        GPIOR0.value = i\n");
        Assert.Equal(new List<int> { 0, 1, 2, 3 }, RegisterWrites(ir));
    }

    [Fact]
    public void ABoundPastTheUnrollLimit_StaysALoop()
    {
        var ir = Gen(
            "from pymcu.types import uint8\n" +
            "from pymcu.chips.atmega328p import GPIOR0\n" +
            "\n" +
            "def main():\n" +
            "    for i in range(40 // 2):\n" +
            "        GPIOR0.value = i\n");
        Assert.True(HasLoop(ir), "twenty iterations are a loop, not twenty copies of the body");
    }

    [Fact]
    public void ABoundTheProgramReallyDecidesAtRunTime_StaysALoop()
    {
        var ir = Gen(
            "from pymcu.types import uint8\n" +
            "from pymcu.chips.atmega328p import GPIOR0\n" +
            "\n" +
            "def main():\n" +
            "    n: uint8 = GPIOR0.value\n" +
            "    for i in range(n // 4):\n" +
            "        GPIOR0.value = i\n");
        Assert.True(HasLoop(ir));
    }

    [Fact]
    public void TheLoopVariableAfterAnEmptyRange_IsNotRewrittenToStart()
    {
        // CPython never binds the loop variable for an empty range at all -- not even to
        // `start` -- so a read after the loop must not be rewritten to the range's start
        // value. `i` is never bound before this loop either, so CPython itself would raise
        // NameError on the read; nothing here models that, but writing a plausible-looking
        // wrong constant (5) is worse than leaving the name unbound, which is what this
        // guards against (PyMCU-review round 3, the inner-empty-range test below is the
        // reported, CPython-matching shape).
        var ir = Gen(
            "from pymcu.types import uint8\n" +
            "from pymcu.chips.atmega328p import GPIOR0\n" +
            "\n" +
            "def main():\n" +
            "    for i in range(5, 5):\n" +
            "        GPIOR0.value = 1\n" +
            "    GPIOR0.value = i\n");
        Assert.DoesNotContain(Main(ir).Body,
            i => i is Copy { Src: Constant { Value: 5 }, Dst: Variable { Name: "main.i" } });
    }

    [Fact]
    public void AnInnerEmptyRange_DoesNotStompTheOuterLoopsReusedName()
    {
        // `for v in (1, 2): for v in range(0): pass; print(v)` left v at 0, 0 on both
        // iterations instead of 1, 2 (PyMCU-review round 3): the empty inner range
        // unconditionally wrote its own start (0) over the outer loop's `v` whenever `v`
        // was read afterwards, even though Python's for-loop machinery never assigns to
        // the target when the iterable has no elements to produce. A nested for-loop in
        // the outer body is never cheap enough to unroll (CheapBodyShape), so the outer
        // loop here is a run-time counter loop reading a flash constant table, not a
        // compile-time fold -- the oracle probe (both front ends, against CPython) is
        // what actually proves the VALUES this lowering produces are right; the IR-level
        // assertion here only has to rule out the specific wrong write the bug made.
        var ir = Gen(
            "from pymcu.types import uint8\n" +
            "from pymcu.chips.atmega328p import GPIOR0\n" +
            "\n" +
            "def main():\n" +
            "    for v in (1, 2):\n" +
            "        for v in range(0):\n" +
            "            pass\n" +
            "        GPIOR0.value = v\n\n" +
            "main()\n");
        Assert.DoesNotContain(Main(ir).Body,
            i => i is Copy { Src: Constant { Value: 0 }, Dst: Variable { Name: "main.v" } });
    }

    [Fact]
    public void ASeparateEmptyRange_DoesNotStompAnEarlierUnrolledLoopsLastValue()
    {
        // The same leak, without a nested loop forcing the table-based lowering above:
        // `for v in (1, 2): pass` leaves v's compile-time binding at 2 on purpose (case 3),
        // and a SEPARATE, later `for v in range(0): pass` reusing the bare name must not
        // erase it just by being entered -- Python never touches `v` for an empty range,
        // so a read afterwards still sees the tuple's last element.
        var ir = Gen(
            "from pymcu.types import uint8\n" +
            "from pymcu.chips.atmega328p import GPIOR0\n" +
            "\n" +
            "def main():\n" +
            "    for v in (1, 2):\n" +
            "        pass\n" +
            "    for v in range(0):\n" +
            "        pass\n" +
            "    GPIOR0.value = v\n\n" +
            "main()\n");
        Assert.Equal(new List<int> { 2 }, RegisterWrites(ir));
    }
}
