using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU#472's remaining shape. The issue was filed as a member SUBSCRIPT store falling into
/// the chip-register bit-index rule; the verbatim repro compiles today, and what is left is
/// not about subscripts at all.
///
/// An instance passed to a parameter that declares no type arrives as a plain number, so ANY
/// member access on it fails -- `o.a[i] = v`, `o.a[i]` and a bare `o.x` alike. Annotating the
/// parameter with its class makes all three compile. The collapse itself is PyMCU#256 and is
/// not fixed here; what is fixed is the message, which named the number rather than the
/// parameter and left the reader with no way to see that an annotation was the difference.
/// </summary>
public class InstanceThroughUnannotatedParameterTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });

    private const string ClassC =
        "from pymcu.types import uint8\n" +
        "class C:\n" +
        "    def __init__(self):\n" +
        "        self.a: uint8[4] = [0] * 4\n" +
        "        self.x: uint8 = 7\n" +
        "\n";

    [Theory]
    [InlineData("def poke(o, i: uint8, v: uint8):\n    o.a[i] = v\n\nc = C()\npoke(c, 1, 9)\n", "a")]
    [InlineData("def peek(o, i: uint8) -> uint8:\n    return o.a[i]\n\nc = C()\ny: uint8 = peek(c, 1)\n", "a")]
    [InlineData("def field(o) -> uint8:\n    return o.x\n\nc = C()\ny: uint8 = field(c)\n", "x")]
    public void AMemberOfAnUnannotatedParameter_SaysToAnnotateTheParameter(string body, string member)
    {
        var ex = Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(ClassC + body));

        Assert.Contains($"'o' is a number here, so it has no member '{member}'", ex.Message);
        Assert.Contains($"'{member}' is a field of C", ex.Message);
        Assert.Contains("o: C", ex.Message);
    }

    [Theory]
    [InlineData("def poke(o: C, i: uint8, v: uint8):\n    o.a[i] = v\n\nc = C()\npoke(c, 1, 9)\n")]
    [InlineData("def peek(o: C, i: uint8) -> uint8:\n    return o.a[i]\n\nc = C()\ny: uint8 = peek(c, 1)\n")]
    [InlineData("def field(o: C) -> uint8:\n    return o.x\n\nc = C()\ny: uint8 = field(c)\n")]
    public void TheSameBodyWithTheAnnotation_Compiles(string body)
        => Assert.NotNull(Gen(ClassC + body));

    [Fact]
    public void AMemberOfARealNumber_KeepsTheShortMessage()
    {
        // No class declares 'foo', so there is no annotation to suggest and the program
        // really is asking a number for an attribute.
        var ex = Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(
            "from pymcu.types import uint8\n" +
            "def f(n: uint8) -> uint8:\n" +
            "    return n.foo\n" +
            "y: uint8 = f(3)\n"));

        Assert.Contains("'foo' is not a member of a numeric value", ex.Message);
    }
}
