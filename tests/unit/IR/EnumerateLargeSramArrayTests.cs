using FluentAssertions;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// <c>for i, b in enumerate(buf)</c> over an SRAM-resident array used to unroll the body once
/// per element with no limit -- the <c>ConstSequenceUnrollLimit</c> that caps list and range
/// unrolling at 8 was never consulted. A 513-byte framebuffer write produced 513 copies of the
/// body and overflowed ATmega328P flash. Past the limit the loop now lowers the way
/// <c>for b in buf[0:n]</c> already does: a counter over <c>range(n)</c> and one indexed load.
/// </summary>
public class EnumerateLargeSramArrayTests
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
        "from pymcu.types import uint8, inline\n" +
        "from pymcu.chips.atmega328p import GPIOR0\n\n";

    private static List<ArrayLoad> LoadsOn(ProgramIR ir, string array) =>
        ir.Functions.SelectMany(f => f.Body).OfType<ArrayLoad>()
            .Where(l => l.ArrayName == array).ToList();

    [Fact]
    public void ABufferPastTheUnrollLimit_IteratesWithARuntimeIndex()
    {
        var ir = Gen(Hdr +
            "buf: uint8[300] = [0] * 300\n" +
            "def main():\n" +
            "    for i, b in enumerate(buf):\n" +
            "        buf[i] = b\n" +
            "    GPIOR0.value = buf[7]\n");

        var loads = LoadsOn(ir, "buf");
        loads.Where(l => l.Index is Variable).Should().NotBeEmpty(
            because: "a 300-element SRAM array is read by one loop body, not unrolled");
        loads.Where(l => l.Index is Constant k && k.Value == 299).Should().BeEmpty(
            because: "unrolling would have emitted a constant-index load for every element");
        ir.Functions.SelectMany(f => f.Body).OfType<ArrayStore>()
            .Where(s => s.Index is Variable).Should().NotBeEmpty(
                because: "the loop body's indexed store must share the runtime index");
    }

    [Fact]
    public void TheCounter_IsWideEnoughForTheArray()
    {
        var ir = Gen(Hdr +
            "buf: uint8[300] = [0] * 300\n" +
            "def main():\n" +
            "    for i, b in enumerate(buf):\n" +
            "        buf[i] = b\n" +
            "    GPIOR0.value = buf[7]\n", optimize: false);

        ir.Functions.SelectMany(f => f.Body)
            .OfType<ArrayLoad>()
            .Where(l => l.ArrayName == "buf")
            .Select(l => l.Index).OfType<Variable>()
            .Select(v => v.Type)
            .Should().AllSatisfy(t => t.Should().Be(DataType.UINT16,
                because: "the counter must reach 299; a uint8 wraps at 255 and the loop never ends"));
    }

    [Fact]
    public void ABufferInsideTheUnrollLimit_StillUnrolls()
    {
        var ir = Gen(Hdr +
            "buf: uint8[4] = [0] * 4\n" +
            "def main():\n" +
            "    for i, b in enumerate(buf):\n" +
            "        buf[i] = b\n" +
            "    GPIOR0.value = buf[0]\n");

        LoadsOn(ir, "buf").Where(l => l.Index is Variable).Should().BeEmpty(
            because: "a short array still folds each element at compile time");
    }
}
