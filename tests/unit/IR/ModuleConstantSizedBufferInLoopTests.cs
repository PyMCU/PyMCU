using System.Linq;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// `bytearray(NB)` with `NB = 8` at the top of the entry file, written in a method of one of its
/// classes and reached through a call inside a loop. The constant is filed under the bare name,
/// the method's module prefix is the class's (`Bus_`), and the loop dropped the flow fact that
/// had been answering, so the evaluator found no value and refused with "a runtime-sized
/// bytearray(n) here would allocate a new region every time". A literal 8 compiled.
/// </summary>
public class ModuleConstantSizedBufferInLoopTests
{
    [Theory]
    [InlineData("avr")]
    [InlineData("arm")]
    public void AModuleConstantSizesABufferInAMethodCalledInALoop(string arch)
    {
        var src =
            "NB = 8\n" +
            "class Bus:\n" +
            "    def search(self, diff: int) -> int:\n" +
            "        rom = bytearray(NB)\n" +
            "        rom[0] = diff\n" +
            "        return rom[0]\n" +
            "    def scan(self) -> int:\n" +
            "        diff = 3\n" +
            "        for _ in range(3):\n" +
            "            diff = self.search(diff)\n" +
            "        return diff\n" +
            "b = Bus()\n" +
            "r = b.scan()\n";
        var ir = new IRGenerator().Generate(new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new System.Collections.Generic.Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = arch });
        Assert.Contains(ir.Functions.SelectMany(f => f.Body), i => i is ArrayStore { Count: 8 });
    }
}
