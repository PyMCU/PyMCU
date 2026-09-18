using System.Linq;
using FluentAssertions;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// Adafruit framebuf writes
/// <c>framebuf.buf[i:i+3] = bytes(fill)</c> after
/// <c>fill = (color&gt;&gt;16)&amp;255, ...</c>.
/// Slice assign onto a module bytearray, <c>bytes(named_seq)</c> as the
/// source, a run-time start of compile-time length, and a member buffer
/// dest all have to lower as an element-wise copy.
/// </summary>
public class FramebufSliceAssignTests
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
    public void BytearraySliceAssign_FromListLiteral_StoresEachByte()
    {
        var ir = Gen(
            "buf = bytearray(9)\n" +
            "buf[0:3] = [10, 20, 30]\n" +
            "def main():\n" +
            "    n = buf[1]\n");

        LastStored(ir, 0).Should().Be(new Constant(10),
            because: "buf = bytearray(N) is slice-assignable; buf[0:3] = [10, 20, 30] writes 10 at slot 0");
        LastStored(ir, 1).Should().Be(new Constant(20),
            because: "slot 1 is 20");
        LastStored(ir, 2).Should().Be(new Constant(30),
            because: "slot 2 is 30");
    }

    [Fact]
    public void BytesOfANamedTuple_IsASliceSource()
    {
        var ir = Gen(
            "from pymcu.types import uint32\n" +
            "buf = bytearray([0, 0, 0])\n" +
            "color: uint32 = 0x112233\n" +
            "fill = (color >> 16) & 255, (color >> 8) & 255, color & 255\n" +
            "buf[0:3] = bytes(fill)\n" +
            "def main():\n" +
            "    n = buf[1]\n");

        LastStored(ir, 0).Should().Be(new Constant(0x11),
            because: "bytes(fill) is the three color bytes; red of 0x112233 is 0x11");
        LastStored(ir, 1).Should().Be(new Constant(0x22),
            because: "green is 0x22");
        LastStored(ir, 2).Should().Be(new Constant(0x33),
            because: "blue is 0x33");
    }

    [Fact]
    public void RuntimeStartOfCompileTimeLength_CopiesThreeBytes()
    {
        var ir = Gen(
            "from pymcu.types import uint8, uint32\n" +
            "buf = bytearray(6)\n" +
            "def blit(i: uint8, color: uint32):\n" +
            "    fill = (color >> 16) & 255, (color >> 8) & 255, color & 255\n" +
            "    buf[i:i+3] = bytes(fill)\n" +
            "def main():\n" +
            "    blit(0, 0x112233)\n" +
            "    blit(3, 0x112233)\n");

        ir.Should().NotBeNull(
            because: "buf[i:i+3] = bytes(fill) has a run-time start and a compile-time length of 3");
        Body(ir).OfType<ArrayStore>().Should().NotBeEmpty(
            because: "the copy lowers to indexed stores into buf, not a refused slice assign");
    }

    [Fact]
    public void MemberBufferSliceAssign_Compiles()
    {
        var ir = Gen(
            "from pymcu.types import uint8\n" +
            "class FB:\n" +
            "    def __init__(self, b):\n" +
            "        self.buf = b\n" +
            "    def paint(self, i: uint8):\n" +
            "        fill = 10, 20, 30\n" +
            "        self.buf[i:i+3] = bytes(fill)\n" +
            "buf = bytearray(6)\n" +
            "fb = FB(buf)\n" +
            "def main():\n" +
            "    fb.paint(0)\n" +
            "    n = buf[1]\n");

        ir.Should().NotBeNull(
            because: "self.buf[i:i+3] = bytes(fill) is the adafruit_framebuf dest: a member array");
    }
}
