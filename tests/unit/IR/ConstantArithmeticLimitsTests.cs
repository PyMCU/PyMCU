using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU#102 and PyMCU#107: two constant-arithmetic answers the compiler used to invent.
/// `a // 0` reached the division routine and handed back 255, and a constant that leaves
/// int32 wrapped around instead of being reported, while the same overflow one width down
/// was already a build error.
/// </summary>
public class ConstantArithmeticLimitsTests
{
    private static ProgramIR Gen(string src)
    {
        var tokens = new Lexer(src).Tokenize();
        var ast = new Parser(tokens).ParseProgram();
        return new IRGenerator().Generate(ast, new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });
    }

    [Fact]
    public void DividingARunTimeValueByLiteralZero_IsReported()
    {
        var ex = Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(
            "def main():\n" +
            "    a: uint8 = 10\n" +
            "    b: uint8 = a\n" +
            "    c: uint8 = b // 0\n"));

        Assert.Contains("division or modulo by zero", ex.Message);
    }

    [Fact]
    public void ModuloByLiteralZero_IsReportedToo()
    {
        var ex = Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(
            "def main():\n" +
            "    a: uint8 = 10\n" +
            "    b: uint8 = a\n" +
            "    c: uint8 = b % 0\n"));

        Assert.Contains("division or modulo by zero", ex.Message);
    }

    [Fact]
    public void AConstantBelowTheInt32Floor_IsReportedInsteadOfWrapping()
    {
        var ex = Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(
            "def main():\n" +
            "    x: int32 = -2147483648 - 1\n"));

        Assert.Contains("does not fit in int32", ex.Message);
    }

    /// <summary>
    /// The ceiling was never the broken side: the literal range check catches it while it is
    /// still a literal. Pinned here so the two ends of the range keep answering.
    /// </summary>
    [Fact]
    public void AConstantAboveTheInt32Ceiling_IsReportedToo()
    {
        var ex = Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(
            "def main():\n" +
            "    x: int32 = 2147483647 + 1\n"));

        Assert.Contains("out of range for int32", ex.Message);
    }

    [Fact]
    public void TheEdgeValuesThemselves_StillCompile()
    {
        Gen("def main():\n" +
            "    hi: int32 = 2147483647\n" +
            "    sum: int32 = 2147483646 + 1\n");
    }

    /// <summary>
    /// int32's own floor cannot be written down: the literal is checked before the minus sign
    /// is applied, so 2147483648 is reported as out of range for the type whose minimum it is.
    /// Pinned as it stands today, with the number the compiler actually prints.
    /// </summary>
    [Fact]
    public void TheInt32Floor_IsStillRejectedByTheLiteralCheck()
    {
        var ex = Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(
            "def main():\n" +
            "    lo: int32 = -2147483648\n"));

        Assert.Contains("2147483648 is out of range for int32", ex.Message);
    }
}
