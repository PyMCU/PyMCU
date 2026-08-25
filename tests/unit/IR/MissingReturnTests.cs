using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU#91: a function that promises a value and can reach the end of its body without a
/// return emitted `ret` with nothing in the return register, and the caller used whatever it
/// held. The shapes that DO cover every path have to keep compiling, which is most of this
/// file: the check is only worth having if it never cries wolf.
/// </summary>
public class MissingReturnTests
{
    private static ProgramIR Gen(string src)
    {
        var tokens = new Lexer(src).Tokenize();
        var ast = new Parser(tokens).ParseProgram();
        return new IRGenerator().Generate(ast, new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });
    }

    [Fact]
    public void AnIfWithNoElse_IsReported()
    {
        var ex = Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(
            "def f(n: uint8) -> uint8:\n" +
            "    if n > 100:\n" +
            "        return 1\n" +
            "\n" +
            "def main():\n" +
            "    x: uint8 = 5\n" +
            "    y: uint8 = f(x)\n"));

        Assert.Contains("'f' is declared to return uint8", ex.Message);
        Assert.Contains("without a return", ex.Message);
    }

    [Fact]
    public void BothBranchesReturning_Compiles()
    {
        Gen("def f(n: uint8) -> uint8:\n" +
            "    if n > 100:\n" +
            "        return 1\n" +
            "    else:\n" +
            "        return 2\n" +
            "\n" +
            "def main():\n" +
            "    x: uint8 = 5\n" +
            "    y: uint8 = f(x)\n");
    }

    [Fact]
    public void AReturnAfterTheIf_Compiles()
    {
        Gen("def f(n: uint8) -> uint8:\n" +
            "    if n > 100:\n" +
            "        return 1\n" +
            "    return 2\n" +
            "\n" +
            "def main():\n" +
            "    x: uint8 = 5\n" +
            "    y: uint8 = f(x)\n");
    }

    [Fact]
    public void AVoidFunctionNeedsNoReturn()
    {
        Gen("def f(n: uint8) -> None:\n" +
            "    if n > 100:\n" +
            "        return\n" +
            "\n" +
            "def main():\n" +
            "    f(5)\n");
    }

    [Fact]
    public void RaisingOnTheRemainingPath_Compiles()
    {
        Gen("def f(n: uint8) -> uint8:\n" +
            "    if n > 100:\n" +
            "        return 1\n" +
            "    raise ValueError(\"too small\")\n" +
            "\n" +
            "def main():\n" +
            "    x: uint8 = 5\n" +
            "    try:\n" +
            "        y: uint8 = f(x)\n" +
            "    except ValueError:\n" +
            "        pass\n");
    }
}
