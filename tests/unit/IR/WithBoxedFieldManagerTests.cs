using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// `with self._device as d:` inside a method, where the owning instance is boxed into an SRAM
/// slot (three or more fields). The boxed field read lowers to a slot-load temporary tagged
/// with the field's class, so the with-manager's alias chain ends at a `tmp_` name. Two gaps
/// compounded here:
///
///   * the scan recorded `self._device = SomeClass(...)` field classes only for a bare-name
///     constructor; the dotted form `i2c_device.I2CDevice(...)` that every bus-device driver
///     spells left `fieldClasses` empty, so the slot-load temp carried no class at all;
///   * the alias chase used by a `with` receiver stopped at any `tmp_` name, even one that
///     did carry a class, so `mgr.__enter__` degraded to an undefined
///     `__with_manager_N___enter__`.
///
/// Reported by adafruit_tcs34725: `self._device = i2c_device.I2CDevice(i2c, address)` beside
/// `_valid` and `integration_time` fields, with `with self._device` expanded inside both the
/// constructor's own call chain and a later method call.
/// </summary>
public class WithBoxedFieldManagerTests
{
    private static ProgramIR Gen(string mainSrc, params (string Name, string Source)[] modules)
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

    // A stand-in for adafruit_bus_device.i2c_device.I2CDevice: a context manager with one
    // callable member the `with` body uses.
    private const string I2cDeviceMod =
        "from pymcu.types import uint8, inline\n\n" +
        "class I2CDevice:\n" +
        "    @inline\n" +
        "    def __init__(self, i2c, address: uint8):\n" +
        "        self._i2c = i2c\n" +
        "        self._addr = address\n" +
        "    @inline\n" +
        "    def __enter__(self):\n" +
        "        return self\n" +
        "    @inline\n" +
        "    def __exit__(self, a=None, b=None, c=None):\n" +
        "        pass\n" +
        "    @inline\n" +
        "    def read_reg(self, reg: uint8) -> uint8:\n" +
        "        return (reg + self._addr) & 0xFF\n";

    // Three fields -> the instance is boxed into a slot; `with self._device` inside `_read`
    // lowers through a slot-load temp.
    private const string Driver =
        "import i2c_device\n\n" +
        "class W:\n" +
        "    def __init__(self, i2c):\n" +
        "        self._device = i2c_device.I2CDevice(i2c, 0x29)\n" +
        "        self._x = 0\n" +
        "        self._y = 0\n" +
        "    def _read(self, reg: uint8) -> uint8:\n" +
        "        with self._device as d:\n" +
        "            return d.read_reg(reg)\n" +
        "    def probe(self) -> uint8:\n" +
        "        return self._read(0x12)\n";

    private static void AssertNoWithManagerCall(ProgramIR ir)
    {
        foreach (var f in ir.Functions)
            foreach (var i in f.Body)
                if (i is Call c)
                    Assert.False(c.FunctionName.Contains("__with_manager_"),
                        $"the manager's method resolved to '{c.FunctionName}', built from the manager name");
    }

    [Fact]
    public void ADottedConstructorField_CompilesThroughAModuleCall()
    {
        var ir = Gen(Driver +
            "w = W(None)\n" +
            "out: uint8 = w.probe()\n",
            ("i2c_device", I2cDeviceMod));
        AssertNoWithManagerCall(ir);
    }

    [Fact]
    public void ADottedConstructorField_CompilesInsideTheConstructorChain()
    {
        var ir = Gen(Driver +
            "w = W(None)\n" +
            "out: uint8 = w._read(0x04)\n",
            ("i2c_device", I2cDeviceMod));
        AssertNoWithManagerCall(ir);
    }

    [Fact]
    public void ABareConstructorField_StillCompiles()
    {
        const string bare =
            "from i2c_device import I2CDevice\n\n" +
            "class W:\n" +
            "    def __init__(self, i2c):\n" +
            "        self._device = I2CDevice(i2c, 0x29)\n" +
            "        self._x = 0\n" +
            "        self._y = 0\n" +
            "    def _read(self, reg: uint8) -> uint8:\n" +
            "        with self._device as d:\n" +
            "            return d.read_reg(reg)\n";
        var ir = Gen(bare +
            "w = W(None)\n" +
            "out: uint8 = w._read(0x04)\n",
            ("i2c_device", I2cDeviceMod));
        AssertNoWithManagerCall(ir);
    }
}
