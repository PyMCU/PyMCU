using PyMCU.Common;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// The recursion refusal is located at the recursive call. It used the line of the entry
/// file's statement that started the outermost expansion, and the file of whatever module
/// was being lowered when it fired: cp-servo's false recursion was reported at pwmio.py:54,
/// a docstring line of a file the program's line 54 does not belong to.
/// </summary>
public class RecursionErrorLocationTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    [Fact]
    public void RealRecursion_IsStillRefused_AtTheRecursiveCall()
    {
        var ex = Assert.Throws<RecursionError>(() => Gen(
            "from pymcu.types import uint8, inline\n" +
            "@inline\n" +
            "def f(x: uint8) -> uint8:\n" +
            "    return f(x)\n" +
            "\n" +
            "\n" +
            "y: uint8 = f(3)\n"));
        // The recursive call is on line 4, where the call to f in f's own body is written;
        // the statement that started the expansion is line 7.
        Assert.Equal(4, ex.Line);
    }
}
