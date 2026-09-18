using FluentAssertions;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// <c>class OLED(framebuf.FrameBuffer)</c> after
/// <c>import adafruit_framebuf as framebuf</c>. Adafruit ssd1306 writes
/// <c>super().__init__(buffer, width, height, _FRAMEBUF_FORMAT)</c> on that
/// dotted base. ResolveCallee used to mangle <c>framebuf_FrameBuffer</c>,
/// inherit nothing, and refuse <c>super()</c> as a missing builtin.
/// </summary>
public class ImportedDottedSuperTests
{
    private static ProgramIR GenImported(string framebuf, string main)
    {
        var imported = new Dictionary<string, ProgramNode>
        {
            ["framebuf"] = new Parser(new Lexer(framebuf).Tokenize()).ParseProgram(),
        };
        return Optimizer.Optimize(new IRGenerator().Generate(
            new Parser(new Lexer(main).Tokenize()).ParseProgram(),
            imported,
            new DeviceConfig { Arch = "avr" },
            projectModules: new HashSet<string> { "framebuf" }));
    }

    private static Val LastStored(ProgramIR ir, int slot) =>
        ir.Functions.SelectMany(f => f.Body).OfType<ArrayStore>()
            .Where(s => s.Index is Constant k && k.Value == slot)
            .Select(s => s.Src).Last();

    private const string FrameBuf =
        "from pymcu.types import uint8\n" +
        "class FrameBuffer:\n" +
        "    def __init__(self, width: uint8):\n" +
        "        self.width: uint8 = width\n";

    [Fact]
    public void SuperInit_OnAnImportedDottedBase_WritesTheBaseField()
    {
        var act = () => GenImported(
            FrameBuf,
            "from pymcu.types import uint8\n" +
            "import framebuf\n" +
            "class OLED(framebuf.FrameBuffer):\n" +
            "    def __init__(self, width: uint8):\n" +
            "        super().__init__(width)\n" +
            "buf = bytearray([0])\n" +
            "o = OLED(10)\n" +
            "buf[0] = o.width\n");

        var ir = act.Should().NotThrow(
            because: "super().__init__ on framebuf.FrameBuffer is the imported class, not builtin super").Subject;
        LastStored(ir, 0).Should().Be(new Constant(10),
            because: "the base __init__ stores width and the subclass reads that field");
    }

    [Fact]
    public void SuperInit_OnAnAliasedImportedModule_IsTheSameClass()
    {
        var imported = new Dictionary<string, ProgramNode>
        {
            ["adafruit_framebuf"] = new Parser(new Lexer(FrameBuf).Tokenize()).ParseProgram(),
        };
        var ir = Optimizer.Optimize(new IRGenerator().Generate(
            new Parser(new Lexer(
                "from pymcu.types import uint8\n" +
                "import adafruit_framebuf as framebuf\n" +
                "class OLED(framebuf.FrameBuffer):\n" +
                "    def __init__(self, width: uint8):\n" +
                "        super().__init__(width)\n" +
                "buf = bytearray([0])\n" +
                "o = OLED(10)\n" +
                "buf[0] = o.width\n").Tokenize()).ParseProgram(),
            imported,
            new DeviceConfig { Arch = "avr" },
            projectModules: new HashSet<string> { "adafruit_framebuf" }));

        LastStored(ir, 0).Should().Be(new Constant(10),
            because: "import adafruit_framebuf as framebuf is how adafruit_ssd1306 spells the base");
    }
}
