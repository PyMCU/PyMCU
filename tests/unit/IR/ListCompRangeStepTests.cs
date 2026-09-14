using PyMCU.Common;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU#287. Both comprehension expanders read range()'s start and stop and ignored the
/// third argument, so `[i for i in range(0, 10, 2)]` yielded every value in [0, 10). With
/// a sized target the length check caught it; with an unsized one it was silent.
/// </summary>
public class ListCompRangeStepTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    private const string Prelude = "from pymcu.types import uint8\n\n";

    [Fact]
    public void AComprehensionOverARange_HonoursTheStep()
    {
        Gen(Prelude + "xs: uint8[5] = [i for i in range(0, 10, 2)]\n");
        var ex = Assert.Throws<CompilerError>(() => Gen(Prelude + "xs: uint8[10] = [i for i in range(0, 10, 2)]\n"));
        Assert.Contains("generated 5", ex.Message);
    }

    [Fact]
    public void ADescendingStep_WalksDown()
    {
        // 9, 6, 3: three values, and the length check says so when the target disagrees.
        Gen(Prelude + "xs: uint8[3] = [i for i in range(9, 0, -3)]\n");
        var ex = Assert.Throws<CompilerError>(() => Gen(Prelude + "xs: uint8[9] = [i for i in range(9, 0, -3)]\n"));
        Assert.Contains("generated 3", ex.Message);
    }

    [Fact]
    public void AZeroStep_IsRefused()
        => Assert.Throws<CompilerError>(() => Gen(Prelude + "xs: uint8[3] = [i for i in range(0, 9, 0)]\n"));
}
