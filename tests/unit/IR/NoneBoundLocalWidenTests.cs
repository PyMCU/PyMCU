using FluentAssertions;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// A variable first bound to <c>None</c> and then assigned a constant keeps that constant's
/// width. Both widening switches -- the inline-local one and the none-valued rebinding one --
/// mapped a <c>Constant</c> to UNKNOWN, so <c>end: Optional[int] = None</c> rebound to
/// <c>len(buf)</c> stayed uint8 and the optimizer wrapped <c>513</c> to its low byte:
/// the SSD1306 framebuffer write sent one byte instead of the whole frame.
/// </summary>
public class NoneBoundLocalWidenTests
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

    // The driver shape: an optional `end` that defaults to len(buf), then gates the write of
    // every byte. `sink` gives `b` somewhere to go so the loop is not dead.
    private const string Writer =
        "@inline\n" +
        "def write(buf: bytearray, end: Optional[int] = None):\n" +
        "    if end is None:\n" +
        "        end = len(buf)\n" +
        "    i: int = 0\n" +
        "    while i < end:\n" +
        "        GPIOR0.value = buf[i]\n" +
        "        i = i + 1\n";

    [Fact]
    public void AConstantRebindingANoneLocal_KeepsItsWidth()
    {
        var ir = Gen(Hdr + Writer +
            "buf = bytearray(513)\n" +
            "def main():\n" +
            "    write(buf)\n");

        ir.Functions.SelectMany(f => f.Body).OfType<Copy>()
            .Where(c => c.Src is Constant k && k.Value == 513)
            .Select(c => c.Dst).OfType<Variable>()
            .Should().Contain(v => v.Name.EndsWith(".end") && v.Type == DataType.UINT16,
                because: "513 does not fit in a byte; the copy must land in a widened variable");
        ir.Functions.SelectMany(f => f.Body).OfType<Copy>()
            .Where(c => c.Dst is Variable v
                    && v.Name.EndsWith(".end") && c.Src is Constant k && k.Value == 1)
            .Should().BeEmpty(
                because: "513 & 0xFF == 1 is the wrap this fix removes");
    }

    [Fact]
    public void TheSameConstantOutsideAnInline_WidensToo()
    {
        var ir = Gen(Hdr +
            "def main():\n" +
            "    end = None\n" +
            "    end = 513\n" +
            "    i: int = 0\n" +
            "    while i < end:\n" +
            "        GPIOR0.value = i\n" +
            "        i = i + 1\n");

        ir.Functions.SelectMany(f => f.Body).OfType<Copy>()
            .Where(c => c.Src is Constant k && k.Value == 513)
            .Select(c => c.Dst).OfType<Variable>()
            .Should().Contain(v => v.Type == DataType.UINT16,
                because: "the plain none-valued rebinding path had the same UNKNOWN-width gap");
    }
}
