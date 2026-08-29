using Xunit;
using PyMCU.Common;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;

namespace PyMCU.UnitTests;

/// <summary>
/// Where the caret lands for the diagnostics `Call.cs` raises (issue #177). A call is the
/// construct with the most parts a diagnostic can be about, and the message names one of them:
/// the callee, one argument, or the method after the dot. Reporting the statement's line and no
/// column left the reader to work out which.
///
/// The sites here were probed one at a time before they were touched, and the ones that stay
/// caretless are as deliberate as the ones that gained a caret. Each abstention has its own test
/// at the bottom saying what would have to change for a column to be honest there.
/// </summary>
public class CallColumnTests
{
    private const string Prelude =
        "def uart_write_str(s: const[str]):\n" +
        "    pass\n" +
        "def uart_write(c: uint8):\n" +
        "    pass\n" +
        "def uart_write_decimal_u8(v: uint8):\n" +
        "    pass\n" +
        "def uart_write_fmt(v: uint16, width: uint8, radix: uint8, pad: uint8, upper: uint8):\n" +
        "    pass\n";

    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(Prelude + src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    private static CompilerError Fails(string src) =>
        Assert.ThrowsAny<CompilerError>(() => Gen(src));

    /// The 1-based column of <paramref name="text"/> on the given line of the fixture, so the
    /// expectation reads as "under that text" and survives the fixture moving.
    private static int ColumnOf(string src, int line, string text)
    {
        string[] lines = (Prelude + src).Split('\n');
        int i = lines[line - 1].IndexOf(text, StringComparison.Ordinal);
        Assert.True(i >= 0, $"'{text}' is not on line {line}: {lines[line - 1]}");
        return i + 1;
    }

    /// Asserts the diagnostic lands on the line containing <paramref name="onLine"/>, under
    /// <paramref name="text"/>. The line is found by its own text rather than counted, so the
    /// shared Prelude can grow without renumbering every expectation.
    private static void PointsAt(string src, string onLine, string text)
    {
        var ex = Fails(src);
        string[] lines = (Prelude + src).Split('\n');
        int line = Array.FindIndex(lines, l => l.Contains(onLine, StringComparison.Ordinal)) + 1;
        Assert.True(line > 0, $"no line of the fixture contains '{onLine}'");
        Assert.Equal(line, ex.Line);
        Assert.Equal(ColumnOf(src, line, text), ex.Column);
    }

    // ---- the method after the dot ----------------------------------------------------------

    [Fact]
    public void AnUnsupportedListMethod_PointsAtTheMethod()
    {
        // A MemberAccessExpr is stamped at its MEMBER name, which is the half of `xs.pop` the
        // message is about: `xs` is fine.
        const string src =
            "def main():\n" +
            "    xs: list[uint8] = [1, 2, 3]\n" +
            "    xs.pop()\n";
        PointsAt(src, "xs.pop()", "pop");
    }

    [Fact]
    public void AMethodOnAStringLiteral_PointsAtTheMethod()
    {
        const string src =
            "def main():\n" +
            "    n: uint8 = len(\"abc\".upper())\n";
        PointsAt(src, "\"abc\".upper()", "upper");
    }

    [Fact]
    public void AMethodOnANumberLiteral_PointsAtTheMethod()
    {
        const string src =
            "def main():\n" +
            "    n: uint8 = (5).bit_length()\n";
        PointsAt(src, "bit_length", "bit_length");
    }

    [Fact]
    public void StrJoinUsedAsAnExpression_PointsAtTheMethod()
    {
        const string src =
            "def main():\n" +
            "    n: uint8 = len(\"-\".join([\"a\", \"b\"]))\n";
        PointsAt(src, ".join(", "join");
    }

    [Fact]
    public void AMethodOnAValueWithNoReceiver_PointsAtTheMethod()
    {
        const string src =
            "def mk() -> uint8:\n" +
            "    return 1\n" +
            "def main():\n" +
            "    n: uint8 = mk().upper()\n";
        PointsAt(src, "mk().upper()", "upper");
    }

    // ---- one argument out of several -------------------------------------------------------

    [Fact]
    public void TooManyArgumentsToABaseConstructor_PointsAtTheFirstExtraOne()
    {
        // "it expects 1 argument(s), but 3 were provided" does not say WHICH to delete. The
        // first argument past the declared list is the one to remove, and every one before it
        // is correct, so a caret on the call as a whole would blame all three.
        const string src =
            "class Base:\n" +
            "    def __init__(self, a: uint8):\n" +
            "        self.a: uint8 = a\n" +
            "class Kid(Base):\n" +
            "    def __init__(self):\n" +
            "        super().__init__(1, 2, 3)\n" +
            "def main():\n" +
            "    k = Kid()\n" +
            "    n: uint8 = k.a\n";
        PointsAt(src, "super().__init__", "2");
    }

    [Fact]
    public void AStarArgumentTheCompilerCannotSeeThrough_PointsAtWhatFollowsTheStar()
    {
        const string src =
            "def f(a: uint8, b: uint8) -> uint8:\n" +
            "    return a + b\n" +
            "def main(n: uint8):\n" +
            "    v: uint8 = f(*n)\n";
        PointsAt(src, "f(*n)", "n)");
    }

    [Fact]
    public void ANumericCastOfTextThatIsNotANumber_PointsAtTheText()
    {
        const string src =
            "def main():\n" +
            "    v: uint8 = uint8(\"300\")\n";
        PointsAt(src, "uint8(\"300\")", "\"300\"");
    }

    [Theory]
    [InlineData("float(\"abc\")", "\"abc\"")]
    [InlineData("int(\"1.5\")", "\"1.5\"")]
    public void EveryNumericCastRefusal_PointsAtTheText(string call, string text)
    {
        string src =
            "def main():\n" +
            "    v: uint8 = uint8(" + call + ")\n";
        PointsAt(src, call, text);
    }

    // ---- the callee, when the message is about the call and not about one part ------------

    [Fact]
    public void AMissingArgument_PointsAtTheCallee()
    {
        // Nothing the reader wrote is wrong; what is wrong is what they did not write. The
        // callee is the only part of the call the message can honestly mark.
        const string src =
            "def f(a: uint8, b: uint8 = 2) -> uint8:\n" +
            "    return a + b\n" +
            "def main():\n" +
            "    v: uint8 = f(b=1)\n";
        PointsAt(src, "= f(b=1)", "f(b=1)");
    }

    [Fact]
    public void MinWithAKeyAndNoOperands_PointsAtTheCallee()
    {
        const string src =
            "def keyfn(v: uint8) -> uint8:\n" +
            "    return v\n" +
            "def main():\n" +
            "    v: uint8 = min(key=keyfn)\n";
        PointsAt(src, "min(key=keyfn)", "min");
    }

    [Fact]
    public void TheGeneratorProtocol_PointsAtTheMethod()
    {
        const string src =
            "def counter():\n" +
            "    i: uint8 = 0\n" +
            "    while i < 3:\n" +
            "        yield i\n" +
            "        i = i + 1\n" +
            "def main():\n" +
            "    v: uint8 = counter().send(1)\n";
        PointsAt(src, "counter().send(1)", "send");
    }

    // ---- sites left unlocated on purpose ----------------------------------------------------

    [Fact]
    public void AKeywordDiagnostic_StaysUnlocated_UntilKeywordsAreStamped()
    {
        // The right node is passed. It draws nothing because the parser builds a
        // KeywordArgExpr without a position; by the leaf convention its position would be its
        // NAME token. This pins the ceiling so the change is visible when it happens.
        const string src =
            "def f(a: uint8) -> uint8:\n" +
            "    return a\n" +
            "def main():\n" +
            "    v: uint8 = f(b=1)\n";
        var ex = Fails(src);
        Assert.Contains("unknown keyword argument 'b'", ex.Message);
        Assert.Equal(CompilerError.Unlocated, ex.Column);
    }

    [Fact]
    public void AConstArgumentRefusedWhileBinding_StaysUnlocated()
    {
        // Not "no node available": the argument node is right there. Argument binding runs
        // before the callee's first statement, so the file label has already moved to the
        // callee while the line is still the caller's. Measured across two modules in
        // tests/stdlib/test_diagnostic_names_the_callee.py, where a six-line helper.py is
        // reported at line 12. A column would put a caret on a line that file does not have.
        const string src =
            "from pymcu.types import const, inline\n" +
            "@inline\n" +
            "def hold(n: const[uint8]) -> uint8:\n" +
            "    return n\n" +
            "def main(v: uint8):\n" +
            "    x: uint8 = hold(v)\n";
        var ex = Fails(src);
        Assert.Contains("requires a compile-time constant", ex.Message);
        Assert.Equal(CompilerError.Unlocated, ex.Column);
    }

    [Fact]
    public void AnFStringFormatSpec_StaysUnlocated_BecauseItsNodesCarrySubLexerPositions()
    {
        // Everything between the braces of an f-string is re-lexed by a fresh Lexer over just
        // that text, so the interpolated expression reports line 1 column 1 of the FIELD. The
        // float sibling of this diagnostic did pass that node, and drew its caret under line 1
        // of the file every time. Withheld here rather than aimed at a line nobody wrote.
        const string src =
            "def main(seed: uint8):\n" +
            "    print(f\"{seed:>8}\")\n";
        var ex = Fails(src);
        Assert.Contains("unsupported f-string format spec", ex.Message);
        Assert.Equal(CompilerError.Unlocated, ex.Column);
    }

    [Fact]
    public void TheFloatFStringRefusal_NoLongerPointsAtLineOne()
    {
        // The wrong caret this uncovered. It is a caret removed, not a caret added: the line
        // it drew under was the first line of the file, whatever line the f-string was on.
        const string src =
            "def main():\n" +
            "    x: float = 1.5\n" +
            "    print(f\"{x:3d}\")\n";
        var ex = Fails(src);
        Assert.Contains("not supported for float values", ex.Message);
        Assert.Equal(CompilerError.Unlocated, ex.Column);
        Assert.True(ex.Line > 1, $"the f-string is well past line 1, and this reports line {ex.Line}");
    }
}
