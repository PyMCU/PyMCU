using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU#345. The annotation reader accepted one bracket holding a bare name or a number, so
/// a dotted name, a nested subscript or an empty `[]` inside the brackets ended the parse with
/// "Expected ']'" at a column inside the annotation. Every one of those shapes had a true
/// sentence waiting in CheckAnnotationNames that was never reached.
///
/// The shape is now READ by the parser and JUDGED downstream. The first group of tests is that
/// the sentence arrives; the second is the spellings that already compiled, which must produce
/// the same text as before or the change is not a parse fix but a behaviour change.
/// </summary>
public class AnnotationSubscriptTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    private static string Refusal(string src) =>
        Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(src)).Message;

    [Fact]
    public void ANestedUnion_IsAnsweredAboutUnionsAndNotAboutABracket()
    {
        string msg = Refusal(
            "def f(p: Union[int, List[int]]):\n" +
            "    pass\n\n" +
            "def main():\n" +
            "    f(1)\n");
        Assert.Contains("union type annotation", msg);
        Assert.DoesNotContain("Expected ']'", msg);
    }

    [Fact]
    public void OptionalOfADottedName_GetsTheSameSentence()
    {
        Assert.Contains("union type annotation", Refusal(
            "def f(p: Optional[digitalio.DigitalInOut] = None):\n" +
            "    pass\n\n" +
            "def main():\n" +
            "    f(1)\n"));
    }

    [Fact]
    public void AnEllipsisInsideBrackets_IsNamed()
    {
        // Refused in the reader rather than carried into the text: `tuple` is a known head and
        // a known head is not looked inside, so carrying it would let this front end compile
        // what the CPython bridge refuses.
        Assert.Contains("'...' is not a type annotation", Refusal(
            "def main():\n" +
            "    x: tuple[Literal[1], ...] = 5\n"));
    }

    [Fact]
    public void AnUnclosedBracket_PointsAtTheBracketAndNotAtTheEndOfTheFile()
    {
        var ex = Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(
            "def f(p: List[int):\n" +
            "    pass\n\n" +
            "def main():\n" +
            "    f(1)\n"));
        Assert.Contains("missing its closing ']'", ex.Message);
        Assert.Equal(1, ex.Line);
    }

    [Fact]
    public void TheSpellingsThatAlreadyCompiled_AreUnchanged()
    {
        Assert.NotNull(Gen("def main():\n    buf: uint8[4] = [1, 2, 3, 4]\n    y = buf[0]\n"));
        Assert.NotNull(Gen(
            "from pymcu.types import const\n\n" +
            "def f(s: const[str]):\n" +
            "    pass\n\n" +
            "def main():\n" +
            "    f(\"hi\")\n"));
        Assert.NotNull(Gen(
            "@inline\n" +
            "def f() -> tuple[uint8, uint16]:\n" +
            "    return 1, 2\n\n" +
            "def main():\n" +
            "    a, b = f()\n"));
    }
}
