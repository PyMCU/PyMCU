using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// Which name a rejected keyword argument reports (issue #194).
///
/// The @inline and constructor path reached for the mangled callee, so
/// `SPI(baudrate=1000000)` reported "in call to machine_SPI___init__", a symbol nobody typed
/// and nobody can search for in their own file. The plain-function path a few hundred lines
/// away already printed the user's spelling, so this is one path catching up with its
/// neighbour rather than a new convention.
/// </summary>
public class KeywordCalleeNameTests
{
    private static PyMCU.Common.CompilerError Reject(string src)
        => Assert.ThrowsAny<PyMCU.Common.CompilerError>(() =>
               new IRGenerator().Generate(
                   new Parser(new Lexer(src).Tokenize()).ParseProgram(),
                   new Dictionary<string, ProgramNode>(),
                   new DeviceConfig { Arch = "avr" }));

    [Fact]
    public void AConstructorKeyword_NamesTheClassTheUserWrote()
    {
        var ex = Reject(
            "class Radio:\n" +
            "    def __init__(self, speed: uint8):\n" +
            "        self.speed = speed\n" +
            "\n" +
            "def main():\n" +
            "    r = Radio(baudrate=9600)\n");

        Assert.Contains("'Radio'", ex.Message);
        Assert.Contains("baudrate", ex.Message);
        Assert.DoesNotContain("___init__", ex.Message);
        Assert.DoesNotContain("Radio___init__", ex.Message);
    }

    [Fact]
    public void AnInlineFunctionKeyword_NamesTheFunctionTheUserWrote()
    {
        var ex = Reject(
            "@inline\n" +
            "def scale(v: uint8) -> uint8:\n" +
            "    return v * 2\n" +
            "\n" +
            "def main():\n" +
            "    x: uint8 = scale(nope=3)\n");

        Assert.Contains("'scale'", ex.Message);
        Assert.Contains("nope", ex.Message);
        Assert.DoesNotContain("_scale", ex.Message);
    }

    // The neighbour this borrows from, so the two stay phrased alike.
    [Fact]
    public void APlainFunctionKeyword_StillNamesTheFunction()
    {
        var ex = Reject(
            "def f(a: uint8) -> uint8:\n" +
            "    return a\n" +
            "\n" +
            "def main():\n" +
            "    x: uint8 = f(a=1, zzz=2)\n");

        Assert.Contains("'f'", ex.Message);
        Assert.Contains("zzz", ex.Message);
    }
}
