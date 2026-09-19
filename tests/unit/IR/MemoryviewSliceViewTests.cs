using FluentAssertions;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// <c>memoryview(buf)[k:]</c> as a value is a writable window of
/// <c>buf</c>, not a copy. adafruit_ssd1306 passes
/// <c>memoryview(self.buffer)[1:]</c> into FrameBuffer so byte 0
/// stays the I2C command and <c>MVLSBFormat.fill</c> writes
/// <c>buffer[1:]</c> via <c>len(framebuf.buf)</c> /
/// <c>framebuf.buf[i] = fill</c>. A plain <c>buf[a:b]</c> is still
/// a copy (sht4x's <c>temp_data = self._buffer[0:2]</c>).
/// </summary>
public class MemoryviewSliceViewTests
{
    private static ProgramIR Gen(string src) =>
        Optimizer.Optimize(new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" }));

    private static ProgramIR GenImported(string framebuf, string main)
    {
        var imported = new Dictionary<string, ProgramNode>
        {
            ["adafruit_framebuf"] = new Parser(new Lexer(framebuf).Tokenize()).ParseProgram(),
        };
        return Optimizer.Optimize(new IRGenerator().Generate(
            new Parser(new Lexer(main).Tokenize()).ParseProgram(),
            imported,
            new DeviceConfig { Arch = "avr" },
            projectModules: new HashSet<string> { "adafruit_framebuf" }));
    }

    private static IEnumerable<Instruction> Body(ProgramIR ir) =>
        ir.Functions.SelectMany(f => f.Body);

    private static Val LastStored(ProgramIR ir, int slot) =>
        Body(ir).OfType<ArrayStore>()
            .Where(s => s.Index is Constant k && k.Value == slot)
            .Select(s => s.Src).Last();

    private static IEnumerable<int> ConstantStoresOf(ProgramIR ir, int src, string? arrayNeedle = null) =>
        Body(ir).OfType<ArrayStore>()
            .Where(s => s.Src is Constant c && c.Value == src && s.Index is Constant
                        && (arrayNeedle == null || s.ArrayName.Contains(arrayNeedle)))
            .Select(s => ((Constant)s.Index).Value);

    private const string FrameBuf =
        "from pymcu.types import uint8\n" +
        "MVLSB: uint8 = 0\n" +
        "class MVLSBFormat:\n" +
        "    def fill(framebuf, color: uint8) -> uint8:\n" +
        "        for i in range(len(framebuf.buf)):\n" +
        "            framebuf.buf[i] = color\n" +
        "        return color\n" +
        "class FrameBuffer:\n" +
        "    def __init__(self, buf, width: uint8, height: uint8, buf_format: uint8 = MVLSB):\n" +
        "        self.buf = buf\n" +
        "        self.width = width\n" +
        "        self.height = height\n" +
        "        if buf_format == MVLSB:\n" +
        "            self.format = MVLSBFormat()\n" +
        "    def fill(self, color: uint8) -> uint8:\n" +
        "        return self.format.fill(self, color)\n" +
        "    def buflen(self) -> uint8:\n" +
        "        return len(self.buf)\n";

    [Fact]
    public void ANamedMemoryviewSlice_HasTheWindowLength()
    {
        var ir = Gen(
            "from pymcu.types import uint8\n" +
            "buf = bytearray([64, 1, 2, 3])\n" +
            "mv = memoryview(buf)[1:]\n" +
            "out = bytearray([0])\n" +
            "out[0] = len(mv)\n");

        LastStored(ir, 0).Should().Be(new Constant(3),
            because: "len(memoryview(buf)[1:]) is the window, not the full 4-byte buffer");
    }

    [Fact]
    public void AWriteThroughANamedMemoryviewSlice_HitsTheBaseAtOffset()
    {
        var ir = Gen(
            "from pymcu.types import uint8\n" +
            "buf = bytearray([64, 1, 2, 3])\n" +
            "mv = memoryview(buf)[1:]\n" +
            "mv[0] = 9\n" +
            "out = bytearray([0])\n" +
            "out[0] = buf[1]\n");

        ConstantStoresOf(ir, 9).Should().Equal(new[] { 1 },
            because: "mv[0] = 9 on memoryview(buf)[1:] must store buf[1], not a copy");
    }

    [Fact]
    public void APlainSlice_IsStillACopy()
    {
        var ir = Gen(
            "from pymcu.types import uint8\n" +
            "buf = bytearray([64, 1, 2, 3])\n" +
            "t = buf[1:]\n" +
            "t[0] = 9\n");

        ConstantStoresOf(ir, 9).Should().NotContain(1,
            because: "buf[1:] is a copy; t[0] = 9 must not store buf[1] (sht4x)");
    }

    [Fact]
    public void SuperInitWithAMemoryviewSlice_FillsFromOffsetAndKeepsTheControlByte()
    {
        var ir = GenImported(
            FrameBuf,
            "from pymcu.types import uint8\n" +
            "import adafruit_framebuf as framebuf\n" +
            "_FRAMEBUF_FORMAT = framebuf.MVLSB\n" +
            "class OLED(framebuf.FrameBuffer):\n" +
            "    def __init__(self):\n" +
            "        self.buffer = bytearray([64, 1, 2, 3])\n" +
            "        super().__init__(memoryview(self.buffer)[1:], 3, 8, _FRAMEBUF_FORMAT)\n" +
            "    def byte0(self) -> uint8:\n" +
            "        return self.buffer[0]\n" +
            "    def byte1(self) -> uint8:\n" +
            "        return self.buffer[1]\n" +
            "fb = OLED()\n" +
            "out = bytearray([0, 0, 0])\n" +
            "out[0] = fb.buflen()\n" +
            "out[1] = fb.fill(7)\n" +
            "out[2] = fb.byte0()\n");

        LastStored(ir, 0).Should().Be(new Constant(3),
            because: "len(framebuf.buf) after super().__init__(memoryview(self.buffer)[1:]) is 3");
        ConstantStoresOf(ir, 7, "buffer").Should().Equal(new[] { 1, 2, 3 },
            because: "MVLSBFormat.fill must write buffer[1:], leaving the 0x40 control byte");
    }

    // The real driver is I2C -> _SSD1306(buffer) -> FrameBuffer(buffer).
    // The second super sees a Variable parameter, not the __view_N Val.
    [Fact]
    public void TwoHopSuper_ForwardsTheMemoryviewWindow()
    {
        var ir = GenImported(
            FrameBuf,
            "from pymcu.types import uint8\n" +
            "import adafruit_framebuf as framebuf\n" +
            "_FRAMEBUF_FORMAT = framebuf.MVLSB\n" +
            "class _SSD1306(framebuf.FrameBuffer):\n" +
            "    def __init__(self, buffer, width: uint8, height: uint8):\n" +
            "        super().__init__(buffer, width, height, _FRAMEBUF_FORMAT)\n" +
            "class OLED(_SSD1306):\n" +
            "    def __init__(self):\n" +
            "        self.buffer = bytearray([64, 1, 2, 3])\n" +
            "        super().__init__(memoryview(self.buffer)[1:], 3, 8)\n" +
            "fb = OLED()\n" +
            "out = bytearray([0, 0])\n" +
            "out[0] = fb.buflen()\n" +
            "out[1] = fb.fill(7)\n");

        LastStored(ir, 0).Should().Be(new Constant(3),
            because: "ssd1306's second super must keep the memoryview window length");
        ConstantStoresOf(ir, 7, "buffer").Should().Equal(new[] { 1, 2, 3 },
            because: "fill through I2C -> _SSD1306 -> FrameBuffer still writes buffer[1:]");
    }
}
