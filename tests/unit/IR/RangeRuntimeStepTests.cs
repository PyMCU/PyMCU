using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU#286. The direction of a range loop's exit test was decided at compile time from a
/// constant step only, so a step held in a variable always got the ascending test and
/// range(10, 0, step) with step = -2 exited before its first iteration.
///
/// A signed runtime step now picks the test at run time; an unsigned one cannot be negative
/// and keeps the single compare.
/// </summary>
public class RangeRuntimeStepTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    private static List<Instruction> Body(string stepType) =>
        Gen("from pymcu.types import uint8, uint16, int8\n\n" +
            $"def count(step: {stepType}) -> uint16:\n" +
            "    c: uint16 = 0\n" +
            "    for i in range(10, 0, step):\n" +
            "        c = c + 1\n" +
            "    return c\n\n" +
            "def main():\n" +
            "    count(1)\n\n" +
            "main()\n")
        .Functions.Single(f => f.Name == "count").Body;

    [Fact]
    public void ASignedRuntimeStep_TestsItsSignBeforeChoosingTheDirection()
    {
        var body = Body("int8");
        Assert.Contains(body, i => i is JumpIfLessThan { Src1: Variable { Name: "count.step" }, Src2: Constant { Value: 0 } });
        Assert.Contains(body, i => i is JumpIfGreaterOrEqual { Src1: Variable { Name: "count.i" } });
        Assert.Contains(body, i => i is JumpIfLessOrEqual { Src1: Variable { Name: "count.i" } });
    }

    [Fact]
    public void AnUnsignedRuntimeStep_KeepsTheSingleAscendingTest()
    {
        var body = Body("uint8");
        Assert.DoesNotContain(body, i => i is JumpIfLessThan);
        Assert.DoesNotContain(body, i => i is JumpIfLessOrEqual);
        Assert.Contains(body, i => i is JumpIfGreaterOrEqual { Src1: Variable { Name: "count.i" } });
    }
}
