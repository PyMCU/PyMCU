using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU#105: an annotation naming no known type used to fall back to uint8 in silence, so a
/// one-character typo truncated the arithmetic to 8 bits and the program printed a different
/// number than the same line without the annotation.
/// </summary>
public class UnknownAnnotationTests
{
    private static ProgramIR Gen(string src)
    {
        var tokens = new Lexer(src).Tokenize();
        var ast = new Parser(tokens).ParseProgram();
        return new IRGenerator().Generate(ast, new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });
    }

    [Fact]
    public void AMisspelledScalar_IsRejectedAndTheRealNameSuggested()
    {
        var ex = Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(
            "def main():\n" +
            "    x: unit8 = 300\n"));

        Assert.Contains("unknown type 'unit8'", ex.Message);
        Assert.Contains("uint8", ex.Message);
    }

    [Fact]
    public void TheSpellingsThatMeanSomething_StillCompile()
    {
        Gen("def main():\n" +
            "    a: uint16 = 300\n" +
            "    b: int8 = -1\n" +
            "    c: float = 1.5\n" +
            "    d: bool = True\n" +
            "    e: uint8[4] = [1, 2, 3, 4]\n");
    }

    [Fact]
    public void AClassOfTheProgramsOwn_IsAType()
    {
        Gen("class Thing:\n" +
            "    def __init__(self, v: uint8):\n" +
            "        self.v: uint8 = v\n" +
            "\n" +
            "def main():\n" +
            "    t: Thing = Thing(3)\n");
    }
}
