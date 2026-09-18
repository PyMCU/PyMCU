using FluentAssertions;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// A field first assigned inside <c>if</c> in a base <c>__init__</c>, reached
/// through <c>super()</c>. Adafruit framebuf writes
/// <c>self.format = MVLSBFormat()</c> in that branch; adafruit_ssd1306
/// forwards with <c>super().__init__(memoryview(...), width, height, fmt)</c>.
/// The super expansion used an <c>inlineN___init___</c> prefix that
/// IsInsideInit did not treat as a constructor, so the write was refused as
/// "has no field 'format'".
/// </summary>
public class SuperInitNestedFieldTests
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
        "class Fmt:\n" +
        "    def __init__(self):\n" +
        "        self.n: uint8 = 7\n" +
        "class FrameBuffer:\n" +
        "    def __init__(self, kind: uint8):\n" +
        "        if kind == 0:\n" +
        "            self.format = Fmt()\n" +
        "        else:\n" +
        "            self.format = Fmt()\n";

    [Fact]
    public void AFieldAssignedInABaseInitIf_IsReachableAfterSuper()
    {
        var ir = Gen(
            FrameBuf +
            "class OLED(FrameBuffer):\n" +
            "    def __init__(self):\n" +
            "        super().__init__(0)\n" +
            "buf = bytearray([0])\n" +
            "o = OLED()\n" +
            "buf[0] = o.format.n\n");

        LastStored(ir, 0).Should().Be(new Constant(7),
            because: "self.format = Fmt() inside the base __init__ if is a constructor field");
    }

    [Fact]
    public void TheSameWhenTheBaseIsAnImportedDottedClass()
    {
        var ir = GenImported(
            FrameBuf,
            "from pymcu.types import uint8\n" +
            "import framebuf\n" +
            "class OLED(framebuf.FrameBuffer):\n" +
            "    def __init__(self):\n" +
            "        super().__init__(0)\n" +
            "buf = bytearray([0])\n" +
            "o = OLED()\n" +
            "buf[0] = o.format.n\n");

        LastStored(ir, 0).Should().Be(new Constant(7),
            because: "class C(framebuf.FrameBuffer) plus super() is the ssd1306 shape");
    }

    [Fact]
    public void TheSameWhenTheImportedModuleIsAliased()
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
                "    def __init__(self):\n" +
                "        super().__init__(0)\n" +
                "buf = bytearray([0])\n" +
                "o = OLED()\n" +
                "buf[0] = o.format.n\n").Tokenize()).ParseProgram(),
            imported,
            new DeviceConfig { Arch = "avr" },
            projectModules: new HashSet<string> { "adafruit_framebuf" }));

        LastStored(ir, 0).Should().Be(new Constant(7),
            because: "import adafruit_framebuf as framebuf is how adafruit_ssd1306 spells the base");
    }
}
