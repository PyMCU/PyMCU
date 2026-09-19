using FluentAssertions;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// <c>import adafruit_framebuf as framebuf</c> then
/// <c>def set_pixel(framebuf, ...): ... framebuf.stride</c>.
/// Adafruit MVLSBFormat names the FrameBuffer parameter <c>framebuf</c>,
/// which is also the import alias in adafruit_ssd1306. The member was
/// mangled as <c>adafruit_framebuf_stride</c> -- a module name that
/// does not exist -- instead of the instance field.
/// </summary>
public class ParamShadowsModuleAliasTests
{
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

    private static Val LastStored(ProgramIR ir, int slot) =>
        ir.Functions.SelectMany(f => f.Body).OfType<ArrayStore>()
            .Where(s => s.Index is Constant k && k.Value == slot)
            .Select(s => s.Src).Last();

    private const string FrameBuf =
        "from pymcu.types import uint8\n" +
        "class FrameBuffer:\n" +
        "    def __init__(self, stride: uint8):\n" +
        "        self.stride: uint8 = stride\n" +
        "class Fmt:\n" +
        "    def set_pixel(self, framebuf, x: uint8):\n" +
        "        return framebuf.stride\n";

    [Fact]
    public void AParamNamedLikeTheImportAlias_ReadsTheInstanceField()
    {
        var ir = GenImported(
            FrameBuf,
            "from pymcu.types import uint8\n" +
            "import adafruit_framebuf as framebuf\n" +
            "fmt = framebuf.Fmt()\n" +
            "fb = framebuf.FrameBuffer(7)\n" +
            "buf = bytearray([0])\n" +
            "buf[0] = fmt.set_pixel(fb, 0)\n");

        LastStored(ir, 0).Should().Be(new Constant(7),
            because: "framebuf.stride inside set_pixel is the instance field, not a module member");
    }

    [Fact]
    public void TheSameForAStaticMethodWhoseFirstParamIsFramebuf()
    {
        var ir = GenImported(
            "from pymcu.types import uint8\n" +
            "class FrameBuffer:\n" +
            "    def __init__(self, stride: uint8):\n" +
            "        self.stride: uint8 = stride\n" +
            "class Fmt:\n" +
            "    @staticmethod\n" +
            "    def set_pixel(framebuf, x: uint8):\n" +
            "        return framebuf.stride\n",
            "from pymcu.types import uint8\n" +
            "import adafruit_framebuf as framebuf\n" +
            "fmt = framebuf.Fmt()\n" +
            "fb = framebuf.FrameBuffer(7)\n" +
            "buf = bytearray([0])\n" +
            "buf[0] = fmt.set_pixel(fb, 0)\n");

        LastStored(ir, 0).Should().Be(new Constant(7),
            because: "MVLSBFormat.set_pixel is a staticmethod whose first param is framebuf");
    }
}
