using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// A field that holds None-or-an-instance is NOT a tagged union: an instance has
/// no member slot (RFC 0009 decision 4), so the None is the field's absence
/// marker and the field stays an object field. The scan used to count a dotted
/// construction (<c>self.f = mod.Cls(...)</c>) as scalar evidence -- the
/// VariableExpr callee arm was the only one that recognised a class -- which
/// made the None+instance field a union and refused the instance write with
/// "field already holds None -- a tagged union member" (adafruit_74hc595's
/// <c>self._device</c>, SPIDevice on the SPI path, None on the bitbang path).
/// </summary>
public class OptionalInstanceFieldTests
{
    private static ProgramIR GenImported(string lib, string main)
    {
        var imported = new Dictionary<string, ProgramNode>
        {
            ["spimod"] = new Parser(new Lexer(lib).Tokenize()).ParseProgram(),
        };
        return new IRGenerator().Generate(
            new Parser(new Lexer(main).Tokenize()).ParseProgram(),
            imported,
            new DeviceConfig { Arch = "avr" },
            projectModules: new HashSet<string> { "spimod" });
    }

    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    private const string SpiMod =
        "class Dev:\n" +
        "    def __init__(self, bus: int):\n" +
        "        self.bus = bus\n" +
        "    def write(self, b: int):\n" +
        "        self.bus = b\n" +
        "def tick(a: int) -> int:\n" +
        "    return a\n";

    [Fact]
    public void NoneOrDottedInstance_StaysAnInstanceField()
    {
        var ir = GenImported(SpiMod,
            "import spimod\n" +
            "class SR:\n" +
            "    def __init__(self, use_spi: bool, bus: int):\n" +
            "        if use_spi:\n" +
            "            self._device = spimod.Dev(bus)\n" +
            "        else:\n" +
            "            self._device = None\n" +
            "            self._bitbang = 0\n" +
            "    def flush(self):\n" +
            "        if self._device is not None:\n" +
            "            self._device.write(1)\n" +
            "        else:\n" +
            "            self._bitbang = 1\n" +
            "sr = SR(True, 3)\n" +
            "sr.flush()\n");

        // No tag byte can exist for an instance payload: `_device` must not mint
        // one, and the build must not refuse the SPIDevice write.
        Assert.DoesNotContain(ir.Globals, g => g.Name.Contains("$tag"));
    }

    [Fact]
    public void NoneOrDottedScalarFunction_StillTags()
    {
        // The other side: `mod.tick()` is a scalar function result, not a class
        // construction, so None + scalar writes remain a tagged union. The
        // `is None` read keeps the tag live past the constant fold.
        var ir = GenImported(SpiMod,
            "import spimod\n" +
            "class M:\n" +
            "    def __init__(self):\n" +
            "        self.v = None\n" +
            "    def poll(self, a: int):\n" +
            "        self.v = spimod.tick(a)\n" +
            "m = M()\n" +
            "m.poll(4)\n" +
            "if m.v is None:\n" +
            "    n = 0\n" +
            "else:\n" +
            "    n = m.v\n");

        Assert.Contains(ir.Globals, g => g.Name == "m_v$tag");
    }

    [Fact]
    public void NoneOrBareInstanceInTheSameModule_StaysAnInstanceField()
    {
        // The bare-name shape the VariableExpr arm already covers, pinned so the
        // dotted fix does not drift from it.
        var ir = Gen(
            "class Dev:\n" +
            "    def __init__(self, bus: int):\n" +
            "        self.bus = bus\n" +
            "class SR:\n" +
            "    def __init__(self, use_spi: bool, bus: int):\n" +
            "        if use_spi:\n" +
            "            self._device = Dev(bus)\n" +
            "        else:\n" +
            "            self._device = None\n" +
            "sr = SR(True, 3)\n");

        Assert.DoesNotContain(ir.Globals, g => g.Name.Contains("$tag"));
    }
}
