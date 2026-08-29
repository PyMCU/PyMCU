using Xunit;
using PyMCU.Common;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;

namespace PyMCU.UnitTests;

/// <summary>
/// A keyword argument to a builtin (issue #226). Every builtin used to let one fall through to
/// its own emitter, which answered in whatever vocabulary that emitter had: `abs(x=1)` as the
/// internal `IR Generation: Unknown Expression type: KeywordArgExpr`, `enumerate(xs, start=1)`
/// as a complaint about the ITERABLE, which was a correct list literal.
///
/// What decides the answer is CPython, not the compiler, and that is what these tests pin.
/// Measured on CPython 3.14:
///
///     abs(x=1)                   TypeError, positional-only
///     len(obj=xs)                TypeError, positional-only
///     range(stop=3)              TypeError, takes no keyword arguments at all
///     min([1, 2], default=0)     runs
///     pow(base=2, exp=3)         runs, 8
///     str(object=1)              runs, '1'
///     enumerate(xs, start=1)     runs
///
/// So there are three answers and not two, and the third is the one a bare "unknown keyword"
/// gets wrong: a name CPython does not have is the reader's typo, and a name it DOES have is
/// PyMCU's gap. Telling someone that `min(default=0)` is unknown says their Python is wrong
/// when it is not.
/// </summary>
public class BuiltinKeywordTests
{
    private const string Prelude =
        "def uart_write_str(s: const[str]):\n" +
        "    pass\n" +
        "def uart_write(c: uint8):\n" +
        "    pass\n" +
        "def uart_write_decimal_u8(v: uint8):\n" +
        "    pass\n" +
        "def rank(v: uint8) -> uint8:\n" +
        "    return v\n";

    private static ProgramIR Gen(string body) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(Prelude + "def main(seed: uint8):\n" + body).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    private static string Refusal(string body) =>
        Assert.ThrowsAny<CompilerError>(() => Gen(body)).Message;

    // ---- CPython takes the name for a positional parameter: the same call, spelled ----------

    [Theory]
    [InlineData("    v: uint8 = pow(base=2, exp=3)\n")]
    [InlineData("    v: uint8 = pow(2, exp=3)\n")]
    [InlineData("    s = str(object=1)\n")]
    public void AKeywordNamingAPositionalParameter_Compiles(string body)
    {
        // `pow(base=2, exp=3)` is 8 in Python and there is nothing for PyMCU to disagree with:
        // the keyword names a positional parameter, so it is the same call written differently.
        Gen(body);
    }

    [Fact]
    public void EnumerateSpelledWithItsParameterName_Compiles()
    {
        Gen("    for i, v in enumerate(iterable=[1, 2]):\n        seed = i + v\n");
    }

    [Fact]
    public void AKeywordAndAPositionalForTheSameParameter_IsRefused()
    {
        Assert.Contains("multiple values for argument 'base'", Refusal("    v: uint8 = pow(2, base=3)\n"));
    }

    // ---- CPython has the keyword and PyMCU does not implement it ---------------------------

    [Theory]
    [InlineData("    v: uint8 = min([1, 2], default=0)\n", "default", "min")]
    [InlineData("    v: uint8 = max([1, 2], default=0)\n", "default", "max")]
    [InlineData("    v: uint8 = sum([1, 2], start=1)\n", "start", "sum")]
    [InlineData("    print(1, flush=True)\n", "flush", "print")]
    public void AKeywordPythonHasAndPyMCUDoesNot_SaysSo(string body, string kw, string fn)
    {
        // The distinction the whole table exists for. These are valid Python, so the message
        // must not call them unknown; `min(default=0)` and `print(flush=True)` both were.
        string msg = Refusal(body);
        Assert.Contains($"'{kw}' is a keyword argument of {fn}() in Python", msg);
        Assert.Contains("does not implement it yet", msg);
        Assert.DoesNotContain("unknown keyword", msg);
    }

    [Fact]
    public void EnumerateStart_SaysSo_RatherThanBlamingTheIterable()
    {
        // The one in this class a real program would write, and the worst answer of the set:
        // the old message was about the ITERABLE, which is a correct list literal.
        string msg = Refusal("    for i, v in enumerate([1, 2], start=1):\n        seed = i + v\n");
        Assert.Contains("'start' is a keyword argument of enumerate() in Python", msg);
        Assert.DoesNotContain("iterable must be", msg);
    }

    // ---- CPython has no such keyword either: the reader's typo -----------------------------

    [Theory]
    [InlineData("    v: uint8 = abs(x=1)\n", "abs")]
    [InlineData("    v: uint8 = len(obj=[1])\n", "len")]
    [InlineData("    s = chr(i=65)\n", "chr")]
    [InlineData("    v: uint8 = divmod(x=7, y=2)\n", "divmod")]
    public void AKeywordPythonDoesNotHaveEither_IsATypo(string body, string fn)
    {
        string msg = Refusal(body);
        Assert.Contains($"unknown keyword argument", msg);
        Assert.Contains($"'{fn}()'", msg);
        Assert.Contains("no keyword arguments in Python either", msg);
    }

    [Fact]
    public void ATypoOnABuiltinThatDoesHaveKeywords_ListsTheOnesItHas()
    {
        string msg = Refusal("    v: uint8 = min(seed, 1, reverse=1)\n");
        Assert.Contains("'key'", msg);
        Assert.Contains("'default'", msg);
    }

    // ---- the internal message is gone -------------------------------------------------------

    [Theory]
    [InlineData("    v: uint8 = abs(x=1)\n")]
    [InlineData("    v: uint8 = len(obj=[1])\n")]
    [InlineData("    s = chr(i=65)\n")]
    [InlineData("    v: uint8 = min([1, 2], default=0)\n")]
    [InlineData("    v: uint8 = divmod(x=7, y=2)\n")]
    public void NoBuiltinKeywordReportsAnInternalClassName(string body)
    {
        // `IR Generation: Unknown Expression type: X` is the compiler saying it reached a state
        // it does not describe. Printing that for a valid Python spelling trains readers to
        // treat real internal errors as their own fault.
        Assert.DoesNotContain("Unknown Expression type", Refusal(body));
    }

    // ---- what already worked has to keep working --------------------------------------------

    [Theory]
    [InlineData("    v: uint8 = min([1, 2], key=rank)\n")]
    [InlineData("    v: uint8 = max([1, 2], key=rank)\n")]
    [InlineData("    print(1, sep=\",\")\n")]
    [InlineData("    print(1, end=\"\")\n")]
    public void TheKeywordsPyMCUImplements_StillCompile(string body) => Gen(body);

    [Fact]
    public void AUserFunctionsKeywordsAreStillBoundByItsOwnParameterNames()
    {
        // The check runs at the builtin dispatch, so it must not answer for anything else. An
        // earlier version had no such guard and refused every keyword argument in the language.
        Gen("    v: uint8 = rank(v=1)\n");
    }
}
