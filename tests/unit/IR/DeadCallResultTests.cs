using FluentAssertions;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// Dead-code elimination keeps a call whose result is never read -- the side effects are the
/// program -- but the result's temporary used to survive with it and claim storage. On the
/// SSD1306 driver hundreds of unrolled <c>i2c_write</c> results each took a static slot and
/// SRAM overflowed. The call now keeps its effects and drops the dead destination.
/// </summary>
public class DeadCallResultTests
{
    private static ProgramIR Gen(string src, bool optimize = true)
    {
        var ir = new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });
        return optimize ? Optimizer.Optimize(ir) : ir;
    }

    private const string Hdr =
        "from pymcu.types import uint8\n" +
        "from pymcu.chips.atmega328p import GPIOR0\n\n";

    private const string Sink =
        "def sink(v: uint8) -> uint8:\n" +
        "    GPIOR0.value = v\n" +
        "    return v + 1\n\n";

    [Fact]
    public void ACallWhoseResultIsNeverRead_KeepsTheCallAndDropsTheSlot()
    {
        var ir = Gen(Hdr + Sink +
            "def main():\n" +
            "    y = sink(7)\n");

        var calls = ir.Functions.SelectMany(f => f.Body).OfType<Call>()
            .Where(c => c.FunctionName == "sink").ToList();
        calls.Should().ContainSingle(
            because: "the side effect is the write to GPIOR0, so the call itself must stay");
        calls[0].Dst.Should().BeOfType<NoneVal>(
            because: "nothing reads the result, so no storage should be allocated for it");
    }

    [Fact]
    public void ACallWhoseResultIsRead_KeepsItsDestination()
    {
        var ir = Gen(Hdr + Sink +
            "def main():\n" +
            "    y = sink(7)\n" +
            "    GPIOR0.value = y\n");

        ir.Functions.SelectMany(f => f.Body).OfType<Call>()
            .Where(c => c.FunctionName == "sink")
            .Select(c => c.Dst)
            .Should().AllSatisfy(d => d.Should().NotBeOfType<NoneVal>(
                because: "a result that is read still needs the slot that carries it"));
    }
}
