using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU#356 and #357. The spellings a typing-annotated library writes, read as the types this
/// compiler already has.
///
/// `WriteableBuffer` and `ReadableBuffer` are CircuitPython's names for a byte buffer, which is
/// what a `bytearray` parameter already gets here; they were refused as unknown types, and that
/// one refusal is the first blocker of `adafruit_bus_device` and of the three libraries that
/// reach it before anything of their own.
///
/// `Tuple[...]` is `tuple[...]`, answered with "did you mean 'tuple'?" and then refused. And a
/// `...` or a `Literal[...]` inside the brackets was a SyntaxError from the reader, which on
/// `Union[int, List[int], Tuple[int, ...]]` answered the reader about the wrong half of the
/// annotation: the union is the real blocker and has its own sentence.
///
/// Both front ends reach one normaliser, so the tests that matter most here are the ones
/// asserting the SAME text and the SAME refusal from each.
/// </summary>
public class TypingAnnotationSpellingTests
{
    private static string CsAnnotation(string src)
    {
        var prog = new Parser(new Lexer(src).Tokenize()).ParseProgram();
        return prog.Functions[0].Params[0].Type ?? "";
    }

    private static string CsReturn(string src)
    {
        var prog = new Parser(new Lexer(src).Tokenize()).ParseProgram();
        return prog.Functions[0].ReturnType ?? "";
    }

    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    private static string Refusal(string src) =>
        Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(src)).Message;

    // ── #356: the CircuitPython buffer names ────────────────────────────────────────────

    [Fact]
    public void WriteableBufferIsReadAsTheByteBufferType()
    {
        Assert.Equal("bytearray", CsAnnotation("def w(buf: WriteableBuffer):\n    buf[0] = 1\n"));
    }

    [Fact]
    public void ReadableBufferIsReadAsTheByteBufferType()
    {
        Assert.Equal("bytearray", CsAnnotation("def r(buf: ReadableBuffer):\n    x = buf[0]\n"));
    }

    [Fact]
    public void TheDottedSpellingReadsTheSameWay()
    {
        Assert.Equal("bytearray",
            CsAnnotation("def w(buf: circuitpython_typing.WriteableBuffer):\n    buf[0] = 1\n"));
    }

    [Fact]
    public void ABufferAnnotatedParameterCompilesAndIsSubscripted()
    {
        Assert.NotNull(Gen(
            "def fill(buf: WriteableBuffer, v: uint8):\n" +
            "    buf[0] = v\n" +
            "    buf[1] = v\n\n" +
            "def main() -> None:\n" +
            "    b = bytearray(2)\n" +
            "    fill(b, 7)\n"));
    }

    // ── #357: Tuple, ellipsis, Literal ──────────────────────────────────────────────────

    [Fact]
    public void TupleIsReadAsTupleInAReturnAnnotation()
    {
        Assert.Equal("tuple[float,float]",
            CsReturn("def reading() -> Tuple[float, float]:\n    return 1.0, 2.0\n"));
    }

    [Fact]
    public void AMultiValueReturnAnnotatedWithTupleCompiles()
    {
        // @inline because a multi-value return is only supported from one, which is a rule
        // about returning a pair and not about how the pair is spelled in the annotation.
        Assert.NotNull(Gen(
            "from pymcu.types import inline, uint8\n\n" +
            "@inline\n" +
            "def pair() -> Tuple[uint8, uint8]:\n" +
            "    return 1, 2\n\n" +
            "def main() -> None:\n" +
            "    a, b = pair()\n"));
    }

    [Fact]
    public void AnEllipsisElementIsKeptAndTheElementTypeSurvives()
    {
        Assert.Equal("tuple[int,...]", CsAnnotation("def f(xs: Tuple[int, ...]):\n    pass\n"));
    }

    [Fact]
    public void ALiteralElementBecomesTheTypeOfItsFirstValue()
    {
        Assert.Equal("tuple[int,...]",
            CsAnnotation("def f(xs: tuple[Literal[9, 10, 11, 12], ...]):\n    pass\n"));
    }

    [Fact]
    public void AModuleGlobalAnnotatedWithAVariadicLiteralTupleCompiles()
    {
        // adafruit_ds18x20's shape: the elements are in the initialiser, and the annotation is
        // saying nothing the compiler needs.
        Assert.NotNull(Gen(
            "RESOLUTION: tuple[Literal[9, 10, 11, 12], ...] = (9, 10, 11, 12)\n\n" +
            "def main() -> None:\n" +
            "    x = RESOLUTION[0]\n"));
    }

    [Fact]
    public void AnEllipsisInsideAUnionIsAnsweredAboutTheUnion()
    {
        // adafruit_ht16k33's shape. The union is the real blocker; the ellipsis used to be
        // refused first, by the reader, so the reader was answered about the wrong half.
        string msg = Refusal(
            "def build(address: Union[int, List[int], Tuple[int, ...]] = 0x70) -> None:\n" +
            "    pass\n\n" +
            "def main() -> None:\n" +
            "    build(1)\n");
        Assert.Contains("union type annotation", msg);
        Assert.DoesNotContain("'...' is not a type annotation", msg);
    }

    [Fact]
    public void AVariadicTupleInAReturnPositionIsRefusedForItsLength()
    {
        string msg = Refusal(
            "def many() -> Tuple[uint8, ...]:\n" +
            "    return 1, 2\n\n" +
            "def main() -> None:\n" +
            "    a, b = many()\n");
        Assert.Contains("does not say how many values", msg);
    }

    [Fact]
    public void ABareEllipsisAnnotationKeepsItsOwnSentence()
    {
        Assert.Contains("'...' is not a type annotation PyMCU can read", Refusal(
            "def main() -> None:\n" +
            "    x: ... = 1\n" +
            "    y = x\n"));
    }

    [Fact]
    public void ABareTupleNameIsStillNotAType()
    {
        Assert.Contains("unknown type 'Tuple'", Refusal(
            "def f(xs: Tuple):\n    pass\n\ndef main():\n    f(1)\n"));
    }
}
