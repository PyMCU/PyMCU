using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU#325. Registering one routine at two interrupt vectors kept only the last of them:
/// the table holds one vector per routine, and the first registration was overwritten. The
/// vector it had been given was left pointing at `__bad_interrupt`, both enable bits were
/// programmed, and the program was quietly deaf to half the interrupts it asked for.
///
/// Two pins handled by the same code is the ordinary shape for anything watching a pair of
/// lines, so the second registration is now refused where it is written, naming both vectors
/// and the wrapper that fixes it.
/// </summary>
public class IsrTwoVectorsTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    private const string Prelude =
        "from pymcu.types import uint8, compile_isr\n" +
        "from pymcu.chips.atmega328p import GPIOR0\n" +
        "\n" +
        "def step():\n" +
        "    GPIOR0.value = 1\n" +
        "\n";

    [Fact]
    public void OneRoutineAtTwoVectors_IsRefusedNamingBoth()
    {
        var ex = Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(Prelude +
            "def main():\n" +
            "    compile_isr(step, 0x0002)\n" +
            "    compile_isr(step, 0x0004)\n"));

        Assert.Contains("0x0002", ex.Message);
        Assert.Contains("0x0004", ex.Message);
        Assert.Contains("one vector", ex.Message);
    }

    [Fact]
    public void TheSameVectorTwice_IsStillAccepted()
    {
        var ir = Gen(Prelude +
            "def main():\n" +
            "    compile_isr(step, 0x0002)\n" +
            "    compile_isr(step, 0x0002)\n");
        var isr = ir.Functions.Single(f => f.IsInterrupt);
        Assert.Equal(0x0002, isr.InterruptVector);
    }

    [Fact]
    public void TwoRoutinesAtTwoVectors_AreBothRegistered()
    {
        var ir = Gen(Prelude +
            "def step2():\n" +
            "    GPIOR0.value = 2\n" +
            "\n" +
            "def main():\n" +
            "    compile_isr(step, 0x0002)\n" +
            "    compile_isr(step2, 0x0004)\n");

        var vectors = ir.Functions.Where(f => f.IsInterrupt).Select(f => f.InterruptVector).OrderBy(v => v);
        Assert.Equal(new[] { 0x0002, 0x0004 }, vectors);
    }
}
