using PyMCU.Common;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// The untyped-list refusal of `.append()` is located at the call. It took the call node's
/// line, and when the C# front end built that node without one, the generator's `lastLine`,
/// which is -1 inside an expansion: a method's append came out as `main.py:-1:1`.
/// </summary>
public class UntypedListAppendLocationTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    [Fact]
    public void AnAppendInAMethodOnAnUntypedGlobal_IsReportedOnItsLine()
    {
        var ex = Assert.Throws<NameError>(() => Gen(
            "from pymcu.types import uint8\n" +
            "xs = []\n" +
            "class P:\n" +
            "    def __init__(self):\n" +
            "        self.n = 0\n" +
            "    def add(self, v: uint8):\n" +
            "        xs.append(v)\n" +
            "p = P()\n" +
            "p.add(3)\n"));
        Assert.Equal(7, ex.Line);
    }
}
