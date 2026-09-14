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
    public void AnEllipsisOnItsOwn_IsNamed()
    {
        // The sentence moved but did not change (#357). It used to be raised by the reader the
        // moment it saw `...` anywhere inside an annotation, on the grounds that a known head
        // is not looked inside so carrying it would let this front end compile what the CPython
        // bridge refuses. The bridge accepted `Callable[..., None]` all along, so refusing it
        // here WAS the divergence; both readers now carry the text and one site answers for it.
        Assert.Contains("'...' is not a type annotation", Refusal(
            "def main():\n" +
            "    x: ... = 5\n" +
            "    y = x\n"));
    }

    [Fact]
    public void AnEllipsisInsideATupleAnnotation_IsRead()
    {
        // `tuple[Literal[9, 10, 11, 12], ...]` is adafruit_ds18x20's module-level table: the
        // elements are in the initialiser, and the annotation says nothing the compiler needs.
        Assert.NotNull(Gen(
            "RESOLUTION: tuple[Literal[9, 10, 11, 12], ...] = (9, 10, 11, 12)\n\n" +
            "def main():\n" +
            "    x = RESOLUTION[0]\n"));
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
