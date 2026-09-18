using FluentAssertions;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// adafruit_74hc595 defines its own <c>DigitalInOut</c> next to <c>import digitalio</c>
/// and constructs it as <c>DigitalInOut(pin, self)</c>. The local class takes two
/// arguments; digitalio's takes one.
/// </summary>
public class LocalClassShadowsImportedClassTests
{
    private static ProgramIR GenWithModules(string mainSrc, params (string Name, string Source)[] modules)
    {
        var imported = new Dictionary<string, ProgramNode>();
        foreach (var (name, source) in modules)
            imported[name] = new Parser(new Lexer(source).Tokenize()).ParseProgram();

        var mainAst = new Parser(new Lexer(mainSrc).Tokenize()).ParseProgram();
        var ctx = new PyMCU.Common.CompilationContext(new CompilerOptions(
            FilePath: "main.py", OutputPath: "", Arch: "avr", Target: "atmega328p",
            Frequency: 16000000, Configs: [], Includes: [], ResetVector: 0, InterruptVector: 0,
            Verbose: false));
        foreach (var (name, _) in modules) ctx.ProjectModules.Add(name);

        return new IRGenerator().Generate(mainAst, imported, new DeviceConfig { Arch = "avr" },
                                          projectModules: ctx.ProjectModules);
    }

    [Fact]
    public void ALocalDigitalInOutIsNotTheImportedOne()
    {
        const string dio =
            "from pymcu.types import uint8\n" +
            "class DigitalInOut:\n" +
            "    def __init__(self, pin: uint8):\n" +
            "        self.pin = pin\n";

        const string sr =
            "from pymcu.types import uint8\n" +
            "import digitalio\n" +
            "class DigitalInOut:\n" +
            "    def __init__(self, pin: uint8, parent: ShiftRegister):\n" +
            "        self.pin = pin\n" +
            "        self.parent = parent\n" +
            "class ShiftRegister:\n" +
            "    def __init__(self):\n" +
            "        self.n = 1\n" +
            "    def get_pin(self, pin: uint8):\n" +
            "        return DigitalInOut(pin, self)\n";

        var ir = GenWithModules(
            "from sr import ShiftRegister\n" +
            "def main():\n" +
            "    s = ShiftRegister()\n" +
            "    p = s.get_pin(0)\n",
            ("digitalio", dio),
            ("sr", sr));

        ir.Functions.Should().NotBeEmpty(
            because: "adafruit_74hc595.DigitalInOut(pin, self) is the local class, not digitalio's");
    }

    [Fact]
    public void TheEntryFilesImportedDigitalInOutDoesNotWinInsideGetPin()
    {
        // The harness writes `from digitalio import DigitalInOut` then constructs a
        // latch, then `sr.get_pin(0)`. TryImportedAlias used to fall back to that
        // entry-file binding while expanding get_pin, so DigitalInOut(pin, self)
        // was digitalio's 1-argument constructor.
        const string dio =
            "from pymcu.types import uint8\n" +
            "class DigitalInOut:\n" +
            "    def __init__(self, pin: uint8):\n" +
            "        self.pin = pin\n";

        const string sr =
            "from pymcu.types import uint8\n" +
            "import digitalio\n" +
            "class DigitalInOut:\n" +
            "    def __init__(self, pin: uint8, parent: ShiftRegister):\n" +
            "        self.pin = pin\n" +
            "        self.parent = parent\n" +
            "    def get(self) -> uint8:\n" +
            "        return self.pin\n" +
            "class ShiftRegister:\n" +
            "    def __init__(self):\n" +
            "        self.n = 1\n" +
            "    def get_pin(self, pin: uint8):\n" +
            "        return DigitalInOut(pin, self)\n";

        var ir = GenWithModules(
            "from digitalio import DigitalInOut\n" +
            "from sr import ShiftRegister\n" +
            "def main():\n" +
            "    latch = DigitalInOut(0)\n" +
            "    s = ShiftRegister()\n" +
            "    p = s.get_pin(6)\n",
            ("digitalio", dio),
            ("sr", sr));

        ir.Functions.Should().NotBeEmpty(
            because: "get_pin's DigitalInOut(pin, self) is the module's class even when main imported digitalio's");
    }

    [Fact]
    public void AReExportedInlineHelperStillFindsItsModuleSubroutine()
    {
        // pymcu.hal.avr.i2c re-exports i2c_write_bytes; that helper calls _twi_wait
        // in the defining file. Walking every prefix before the import table made
        // `_twi_wait` resolve to a class key, then "undefined function '_twi_wait'".
        const string impl =
            "from pymcu.types import uint8, inline\n" +
            "def _wait() -> uint8:\n" +
            "    return 1\n" +
            "@inline\n" +
            "def write_bytes(n: uint8) -> uint8:\n" +
            "    return _wait()\n";

        const string facade =
            "from impl import write_bytes\n";

        var ir = GenWithModules(
            "from facade import write_bytes\n" +
            "def main():\n" +
            "    write_bytes(1)\n",
            ("impl", impl),
            ("facade", facade));

        ir.Functions.Should().NotBeEmpty(
            because: "a facade that re-exports write_bytes must still resolve _wait in the defining module");
    }
}
