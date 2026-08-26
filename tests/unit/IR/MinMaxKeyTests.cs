using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

// `min(a, b, key=f)` and `max(...)`, issue #190.
//
//     max(3, 1, key=key)  ->  IR Generation: Unknown Expression type: KeywordArgExpr
//
// min and max are lowered before the general keyword path, so their arguments never reached
// ReorderCallArgs, which is where every other call resolves its keywords and reports the bad
// ones by name. The KeywordArgExpr node fell through to VisitExpression, which has one answer
// for a node it does not know: the name of the class.
//
// It compiles now rather than being refused. Nothing about `key=` needs a run-time callable:
// the name resolves at compile time the same way `f = key` already resolved, and the lowering
// is one key call per operand plus the compare-and-select min/max already emits. The reason
// the issue proposed for a refusal, that there are no callable values at run time, is true for
// a function passed through a parameter and is not the situation here.
//
// WHAT DISCRIMINATES: everything that mentions `key`. Against the unfixed compiler the two
// refusals are not raised and the accepted programs die with the class name above.
//
// WHAT IS INVARIANT: min and max without a key, the sequence form, and a keyword argument to
// an ordinary function, which is the path this one deliberately does not touch.
//
// The values are checked on hardware, not here: tests/integration/Tests/AVR/MinMaxKeyTests.cs
// runs a program whose key inverts, so an ignored key answers with the plain comparison.
public class MinMaxKeyTests
{
    private static void Build(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });

    private static PyMCU.Common.CompilerError Reject(string src)
        => Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Build(src));

    private const string Preamble =
        "from pymcu.chips.atmega328p import GPIOR0, GPIOR1\n" +
        "from pymcu.types import uint8\n" +
        "\n" +
        "\n" +
        "def rank(x: uint8) -> uint8:\n" +
        "    return 100 - x\n" +
        "\n" +
        "\n";

    private static string Program(string body) =>
        Preamble +
        "def main():\n" +
        "    seed: uint8 = GPIOR0.value\n" +
        body +
        "    while True:\n" +
        "        pass\n";

    // --- what compiles ---------------------------------------------------------

    [Fact]
    public void MaxTakesAKeyFunction()
    {
        Build(Program("    GPIOR1.value = max(seed, 1, key=rank)\n"));
    }

    [Fact]
    public void MinTakesAKeyFunction()
    {
        Build(Program("    GPIOR1.value = min(seed, 1, key=rank)\n"));
    }

    [Fact]
    public void MoreThanTwoOperandsTakeAKey()
    {
        Build(Program("    GPIOR1.value = max(seed, 1, 7, 3, key=rank)\n"));
    }

    [Fact]
    public void TheSequenceFormTakesAKey()
    {
        Build(Program(
            "    xs: uint8[3] = [30, 10, 70]\n" +
            "    GPIOR1.value = min(xs, key=rank) + seed\n"));
    }

    [Fact]
    public void KeyNoneIsTheSameAsNoKeyAtAll()
    {
        // CPython's spelling for "compare the values themselves", and it must reach the plain
        // lowering rather than trying to call None.
        Build(Program("    GPIOR1.value = min(seed, 1, key=None)\n"));
    }

    // --- what is refused, by name ----------------------------------------------

    [Fact]
    public void AnotherKeywordIsRefusedByItsOwnName()
    {
        var ex = Reject(Program("    GPIOR1.value = max(seed, 1, foo=1)\n"));

        Assert.Contains("'foo'", ex.Message);
        Assert.Contains("max()", ex.Message);
        Assert.DoesNotContain("KeywordArgExpr", ex.Message);
    }

    [Fact]
    public void TheRefusalSaysWhatMinAndMaxDoTake()
    {
        var ex = Reject(Program("    GPIOR1.value = min(seed, 1, reverse=1)\n"));

        Assert.Contains("'key'", ex.Message);
    }

    [Fact]
    public void RepeatingKeyIsRefused()
    {
        var ex = Reject(Program("    GPIOR1.value = min(seed, 1, key=rank, key=rank)\n"));

        Assert.Contains("repeated", ex.Message);
        Assert.DoesNotContain("KeywordArgExpr", ex.Message);
    }

    [Fact]
    public void AKeyThatIsAValueGetsTheNotCallableMessage()
    {
        // The key is called like any other function, so a name bound to a value is reported by
        // the message that already exists for calling one, rather than a second wording of it.
        var ex = Reject(Program("    GPIOR1.value = min(seed, 1, key=seed)\n"));

        Assert.Contains("not callable", ex.Message);
        Assert.DoesNotContain("KeywordArgExpr", ex.Message);
    }

    [Fact]
    public void ASequenceOfUnknownLengthKeepsItsOwnRefusal()
    {
        var ex = Reject(Program("    GPIOR1.value = min(seed, key=rank)\n"));

        Assert.Contains("length known at compile time", ex.Message);
    }

    // --- invariants: the paths this must not have moved ------------------------

    [Fact]
    public void MinAndMaxWithoutAKeyStillCompile()
    {
        Build(Program("    GPIOR1.value = max(seed, 1) + min(seed, 3)\n"));
    }

    [Fact]
    public void TheSequenceFormWithoutAKeyStillCompiles()
    {
        Build(Program(
            "    xs: uint8[3] = [30, 10, 70]\n" +
            "    GPIOR1.value = min(xs) + seed\n"));
    }

    [Fact]
    public void AKeywordArgumentToAnOrdinaryFunctionStillBinds()
    {
        // ReorderCallArgs is the general path, and it is one `if` away from the one changed.
        Build(Preamble +
              "def scale(v: uint8, by: uint8) -> uint8:\n" +
              "    return v * by\n" +
              "\n" +
              "\n" +
              "def main():\n" +
              "    seed: uint8 = GPIOR0.value\n" +
              "    GPIOR1.value = scale(seed, by=2)\n" +
              "    while True:\n" +
              "        pass\n");
    }

    [Fact]
    public void AKeywordArgumentToAnotherBuiltinKeepsItsOwnMessage()
    {
        var ex = Reject(Program(
            "    xs: uint8[3] = [30, 10, 70]\n" +
            "    GPIOR1.value = uint8(len(xs, foo=1)) + seed\n"));

        Assert.DoesNotContain("KeywordArgExpr", ex.Message);
        Assert.Contains("len()", ex.Message);
    }
}
