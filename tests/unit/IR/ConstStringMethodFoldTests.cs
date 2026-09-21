using FluentAssertions;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// Compile-time string methods: a method call on a receiver whose text the compiler holds
/// is a constant-fold, not a call -- there is no string object to dispatch on, and the
/// answer is data the compiler already has. Reduced from adafruit_pixelbuf's
/// parse_byteorder, which runs strip() and index() on the byteorder parameter of an
/// inlined method.
///
/// A receiver whose text is NOT known still reaches the "not supported on a string"
/// refusal (StringMethodDiagnosticTests), as does a method outside this set.
/// </summary>
public class ConstStringMethodFoldTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });

    private static List<Instruction> Main(string src) =>
        Gen(src).Functions.Single(f => f.Name == "main").Body;

    private static bool CopiesText(List<Instruction> body, string text) =>
        body.Any(i => i is Copy { Src: Constant { Text: var t } } && t == text);

    private static bool CopiesInt(List<Instruction> body, int value) =>
        body.Any(i => i is Copy { Src: Constant { Value: var v } } && v == value);

    // ── strip / lstrip / rstrip ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("\"  hi  \".strip()", "hi")]
    [InlineData("\"  hi  \".lstrip()", "hi  ")]
    [InlineData("\"  hi  \".rstrip()", "  hi")]
    [InlineData("\"xxhixx\".strip(\"x\")", "hi")]
    public void StripFolds(string call, string expected)
    {
        var body = Main($"def main():\n    x = {call}\n");
        CopiesText(body, expected).Should().BeTrue($"'{call}' is '{expected}' at compile time");
    }

    // ── index / find ─────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("\"GRB\".index(\"G\")", 0)]
    [InlineData("\"GRB\".index(\"R\")", 1)]
    [InlineData("\"GRB\".index(\"B\")", 2)]
    [InlineData("\"hi\".find(\"z\")", -1)]
    public void IndexAndFindFold(string call, int expected)
    {
        var body = Main($"def main():\n    x: int32 = {call}\n");
        CopiesInt(body, expected).Should().BeTrue($"'{call}' is {expected} at compile time");
    }

    [Fact]
    public void IndexOnAMissRaisesValueError()
    {
        // Not a compile-time abort: lowered as a raise so a try/except around the call --
        // the shape parse_byteorder uses -- catches it exactly as it would at run time.
        var body = Main(
            "def main():\n" +
            "    x: uint8 = 0\n" +
            "    try:\n" +
            "        \"RGB\".index(\"W\")\n" +
            "    except ValueError:\n" +
            "        x = 7\n");
        CopiesInt(body, 7).Should().BeTrue("the except arm runs: index missed, ValueError");
    }

    // ── startswith / endswith / count ────────────────────────────────────────────────────

    [Theory]
    [InlineData("\"hello\".startswith(\"he\")", 1)]
    [InlineData("\"hello\".startswith(\"lo\")", 0)]
    [InlineData("\"hello\".endswith(\"lo\")", 1)]
    [InlineData("\"hello\".endswith(\"he\")", 0)]
    [InlineData("\"a-b-a\".count(\"a\")", 2)]
    [InlineData("\"a-b-a\".count(\"z\")", 0)]
    public void PredicateAndCountFold(string call, int expected)
    {
        var body = Main($"def main():\n    x: int32 = {call}\n");
        CopiesInt(body, expected).Should().BeTrue($"'{call}' is {expected} at compile time");
    }

    // ── replace / upper / lower ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("\"a-b\".replace(\"-\", \"+\")", "a+b")]
    [InlineData("\"hi\".upper()", "HI")]
    [InlineData("\"HI\".lower()", "hi")]
    public void TextProducingMethodsFold(string call, string expected)
    {
        var body = Main($"def main():\n    x = {call}\n");
        CopiesText(body, expected).Should().BeTrue($"'{call}' is '{expected}' at compile time");
    }

    // ── receivers that are not literals ─────────────────────────────────────────────────

    [Fact]
    public void ANameBoundToALiteralFoldsTheSame()
    {
        var body = Main("def main():\n    s = \"GRB\"\n    x: int32 = s.index(\"B\")\n");
        CopiesInt(body, 2).Should().BeTrue("the receiver's text is known through the name");
    }

    [Fact]
    public void AStringParameterInsideAnInlineCallFolds()
    {
        // The pixelbuf shape: parse_byteorder's byteorder parameter is bound to the "GRB"
        // literal at the call site, and the methods run on the parameter name.
        var body = Main(
            "from pymcu.types import inline\n" +
            "@inline\n" +
            "def parse(bo) -> int32:\n" +
            "    return bo.index(\"B\")\n" +
            "def main():\n" +
            "    x: int32 = parse(\"GRB\")\n");
        CopiesInt(body, 2).Should().BeTrue("the parameter is \"GRB\" where the call binds it");
    }

    // ── substring membership ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("\"W\" in \"RGBW\"", 1)]
    [InlineData("\"W\" in \"RGB\"", 0)]
    [InlineData("\"W\" not in \"RGB\"", 1)]
    public void SubstringMembershipFolds(string test, int expected)
    {
        var body = Main($"def main():\n    x: int32 = 1 if {test} else 0\n");
        CopiesInt(body, expected).Should().BeTrue($"'{test}' is {expected != 0} at compile time");
    }

    [Fact]
    public void SubstringMembershipOnABoundName()
    {
        // pixelbuf's `"W" in byteorder` -- the right operand is a name, not a literal.
        var body = Main(
            "def main():\n" +
            "    bo = \"RGBW\"\n" +
            "    x: int32 = 1 if \"W\" in bo else 0\n");
        CopiesInt(body, 1).Should().BeTrue("\"W\" in \"RGBW\" folds through the name");
    }
}
