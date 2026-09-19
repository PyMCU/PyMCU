using FluentAssertions;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// <c>framebuf.buf = [fill for i in range(len(framebuf.buf))]</c>.
/// Adafruit GS2HMSBFormat.fill writes that assignment. A list
/// comprehension in a value position is refused; assigned to a field
/// that already is a fixed array, it fills that storage.
/// </summary>
public class MemberListCompFillTests
{
    private static ProgramIR Gen(string src) =>
        Optimizer.Optimize(new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" }));

    private static List<int> FilledSlots(ProgramIR ir) =>
        ir.Functions.SelectMany(f => f.Body).OfType<ArrayStore>()
            .Where(s => s.ArrayName.Contains("buf", StringComparison.Ordinal)
                        && s.Src is Constant { Value: 9 }
                        && s.Index is Constant idx)
            .Select(s => ((Constant)s.Index).Value)
            .Distinct()
            .ToList();

    [Fact]
    public void AListCompAssignedToAFieldArray_FillsEverySlot()
    {
        var ir = Gen(
            "from pymcu.types import uint8\n" +
            "class FB:\n" +
            "    def __init__(self):\n" +
            "        self.buf: uint8[4] = [1, 2, 3, 4]\n" +
            "    @inline\n" +
            "    def fill(self, color: uint8):\n" +
            "        self.buf = [color for i in range(len(self.buf))]\n" +
            "fb = FB()\n" +
            "fb.fill(9)\n");

        FilledSlots(ir).Should().BeEquivalentTo(new[] { 0, 1, 2, 3 },
            because: "framebuf.buf = [color for i in range(len(framebuf.buf))] writes color into every slot");
    }

    [Fact]
    public void TheSameThroughAStaticMethodWhoseParamIsTheInstance()
    {
        var ir = Gen(
            "from pymcu.types import uint8\n" +
            "class FB:\n" +
            "    def __init__(self):\n" +
            "        self.buf: uint8[4] = [1, 2, 3, 4]\n" +
            "class Fmt:\n" +
            "    @staticmethod\n" +
            "    def fill(framebuf, color: uint8):\n" +
            "        framebuf.buf = [color for i in range(len(framebuf.buf))]\n" +
            "fb = FB()\n" +
            "Fmt.fill(fb, 9)\n" +
            "out = bytearray([0])\n" +
            "out[0] = fb.buf[0]\n");

        FilledSlots(ir).Should().Contain(0,
            because: "GS2HMSBFormat.fill is a staticmethod that writes framebuf.buf");
    }
}
