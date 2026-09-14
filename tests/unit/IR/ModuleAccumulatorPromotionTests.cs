using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// A module-level unannotated accumulator is typed the way a local is: from the promoted
/// width of what feeds it. `n = 0` then `n = n + 1` inside a module-level loop stayed a byte
/// and counted to 44 where the same two lines inside a def counted to 300, and `s = s + i`
/// over range(300) had no width for i at all.
/// </summary>
public class ModuleAccumulatorPromotionTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    private static DataType GlobalType(ProgramIR ir, string name) => ir.Globals.Single(g => g.Name == name).Type;

    [Fact]
    public void ACounterFedByOne_IsSixteenBits()
    {
        var ir = Gen("n = 0\nfor i in range(300):\n    n = n + 1\n");
        Assert.Equal(DataType.UINT16, GlobalType(ir, "n"));
    }

    [Fact]
    public void ASumOfTheLoopVariable_IsWideEnoughForTheSum()
    {
        var ir = Gen("s = 0\nfor i in range(300):\n    s = s + i\n");
        Assert.Equal(DataType.UINT32, GlobalType(ir, "s"));
    }

    [Fact]
    public void ADescendingLoopVariable_StillTypesTheSum()
    {
        var ir = Gen("s = 0\nfor i in range(100, -1, -1):\n    s = s + i\n");
        Assert.True(GlobalType(ir, "s").SizeOf() >= 2, "5050 does not fit a byte");
    }

    [Fact]
    public void ALiteralOnlyGlobal_IsNotWidened()
    {
        var ir = Gen("x = 5\nfor i in range(300):\n    x = 7\n");
        Assert.Equal(DataType.UINT8, GlobalType(ir, "x"));
    }
}
