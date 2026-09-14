using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU#341. A trailing comma was accepted in a call, in a parameter list, in a dict and in
/// a set, and refused in a list literal, where it was reported as a missing expression at the
/// closing bracket. The three neighbours are asserted alongside the list so the next change to
/// one of them cannot quietly take the others with it.
/// </summary>
public class TrailingCommaTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    [Fact]
    public void AListLiteral_AcceptsATrailingComma()
    {
        var ir = Gen(
            "X = [1, 2,]\n\n" +
            "def main():\n" +
            "    y = X[0]\n");
        Assert.NotNull(ir);
    }

    [Fact]
    public void AListLiteralSplitOverLines_AcceptsTheCommaAFormatterWrites()
    {
        var ir = Gen(
            "X = [\n" +
            "    1,\n" +
            "    2,\n" +
            "]\n\n" +
            "def main():\n" +
            "    y = X[0]\n");
        Assert.NotNull(ir);
    }

    [Fact]
    public void ALocalListLiteral_AcceptsATrailingComma()
    {
        var ir = Gen(
            "def main():\n" +
            "    x = [1, 2,]\n" +
            "    y = x[0]\n");
        Assert.NotNull(ir);
    }

    [Fact]
    public void ACallADictAndAParameterList_StillAcceptOne()
    {
        var ir = Gen(
            "D = {1: 2, 3: 4,}\n\n" +
            "def f(\n" +
            "    a: uint8,\n" +
            "    b: uint8,\n" +
            ") -> uint8:\n" +
            "    return a + b\n\n" +
            "def main():\n" +
            "    y = f(1, 2,)\n" +
            "    z = D[1]\n");
        Assert.NotNull(ir);
    }

    [Fact]
    public void ALoneCommaIsStillNotAList()
    {
        // `[,]` has no first element and must stay an error, so the new guard cannot be read
        // as "a comma may start a list".
        Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen("def main():\n    x = [,]\n"));
    }
}
