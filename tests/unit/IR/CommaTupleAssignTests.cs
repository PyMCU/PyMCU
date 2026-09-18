using System.Linq;
using FluentAssertions;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// An unparenthesized tuple RHS is the same fixed array a parenthesized one
/// already is. Adafruit framebuf writes
/// <c>fill = (color >> 16) &amp; 255, (color >> 8) &amp; 255, color &amp; 255</c>
/// and then indexes those three bytes.
/// </summary>
public class CommaTupleAssignIRTests
{
    private static ProgramIR Gen(string src)
    {
        var ir = new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });
        return Optimizer.Optimize(ir);
    }

    private static IEnumerable<Instruction> Body(ProgramIR ir) =>
        ir.Functions.SelectMany(f => f.Body);

    private static Val LastStored(ProgramIR ir, int slot) =>
        Body(ir).OfType<ArrayStore>()
            .Where(s => s.Index is Constant k && k.Value == slot)
            .Select(s => s.Src).Last();

    [Fact]
    public void AnUnparenthesizedTupleRhs_IsIndexable()
    {
        var ir = Gen(
            "buf = bytearray([0, 0, 0])\n" +
            "fill = 10, 20, 30\n" +
            "buf[0] = fill[0]\n" +
            "buf[1] = fill[1]\n" +
            "buf[2] = fill[2]\n");

        LastStored(ir, 0).Should().Be(new Constant(10),
            because: "fill = 10, 20, 30 is a 3-slot array, so fill[0] is 10");
        LastStored(ir, 1).Should().Be(new Constant(20),
            because: "fill[1] is 20");
        LastStored(ir, 2).Should().Be(new Constant(30),
            because: "fill[2] is 30");
    }

    [Fact]
    public void AShiftedColorTuple_StoresEachByte()
    {
        var ir = Gen(
            "from pymcu.types import uint8, uint32\n" +
            "buf = bytearray([0, 0, 0])\n" +
            "color: uint32 = 0x112233\n" +
            "fill = (color >> 16) & 255, (color >> 8) & 255, color & 255\n" +
            "buf[0] = fill[0]\n" +
            "buf[1] = fill[1]\n" +
            "buf[2] = fill[2]\n");

        LastStored(ir, 0).Should().Be(new Constant(0x11),
            because: "(0x112233 >> 16) & 255 is the red byte 0x11");
        LastStored(ir, 1).Should().Be(new Constant(0x22),
            because: "(0x112233 >> 8) & 255 is the green byte 0x22");
        LastStored(ir, 2).Should().Be(new Constant(0x33),
            because: "0x112233 & 255 is the blue byte 0x33");
    }
}
