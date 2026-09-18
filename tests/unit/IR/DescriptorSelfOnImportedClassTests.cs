using FluentAssertions;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// A descriptor read of <c>self.attr</c> from a method of an imported class.
/// CPython rewrites it to <c>type(self).attr.__get__(self, type(self))</c>.
/// The rewrite used the mangled class key (<c>sensor_Dev</c>) as a VariableExpr,
/// which is not a bound name, so <c>self.raw_bus_voltage</c> inside
/// <c>INA219.bus_voltage</c> was "name 'adafruit_ina219_INA219' is not defined".
/// Same stop on veml7700's <c>self.light_gain</c>.
/// </summary>
public class DescriptorSelfOnImportedClassTests
{
    private const string Pack =
        "class Field:\n" +
        "    def __init__(self, addr: uint8) -> None:\n" +
        "        self.addr = addr\n" +
        "    def __get__(self, obj, objtype=None) -> uint8:\n" +
        "        return self.addr + obj.base\n" +
        "    def __set__(self, obj, value: uint8) -> None:\n" +
        "        obj.base = value\n";

    private const string Sensor =
        "from pack import Field\n" +
        "class Dev:\n" +
        "    raw = Field(9)\n" +
        "    def __init__(self, base: uint8) -> None:\n" +
        "        self.base = base\n" +
        "    def scaled(self) -> uint8:\n" +
        "        return self.raw * 2\n" +
        "    @property\n" +
        "    def volts(self) -> uint8:\n" +
        "        return self.raw * 2\n" +
        "    def set_raw(self, value: uint8) -> None:\n" +
        "        self.raw = value\n";

    private static ProgramIR Gen(string main)
    {
        var imported = new Dictionary<string, ProgramNode>
        {
            ["pack"] = new Parser(new Lexer(Pack).Tokenize()).ParseProgram(),
            ["sensor"] = new Parser(new Lexer(Sensor).Tokenize()).ParseProgram(),
        };
        var ir = new IRGenerator().Generate(
            new Parser(new Lexer(main).Tokenize()).ParseProgram(),
            imported,
            new DeviceConfig { Arch = "avr" },
            projectModules: new HashSet<string> { "pack", "sensor" });
        return Optimizer.Optimize(ir);
    }

    private static Val LastStored(ProgramIR ir, int slot) =>
        ir.Functions.SelectMany(f => f.Body).OfType<ArrayStore>()
            .Where(s => s.Index is Constant k && k.Value == slot)
            .Select(s => s.Src).Last();

    [Fact]
    public void ADescriptorReadThroughSelfOnAnImportedClass_IsTheGetAnswer()
    {
        var ir = Gen(
            "from sensor import Dev\n" +
            "buf = bytearray([0])\n" +
            "d = Dev(2)\n" +
            "buf[0] = d.scaled()\n");

        LastStored(ir, 0).Should().Be(new Constant(22),
            because: "self.raw inside Dev.scaled is Field.__get__, so (9+2)*2 is what CPython prints");
    }

    [Fact]
    public void APropertyThatReadsADescriptorOnAnImportedClass_IsTheGetAnswer()
    {
        var ir = Gen(
            "from sensor import Dev\n" +
            "buf = bytearray([0])\n" +
            "d = Dev(2)\n" +
            "buf[0] = d.volts\n");

        LastStored(ir, 0).Should().Be(new Constant(22),
            because: "INA219.bus_voltage is this shape: a @property that reads self.raw_bus_voltage");
    }

    [Fact]
    public void ADescriptorWriteThroughSelfOnAnImportedClass_CallsSet()
    {
        var ir = Gen(
            "from sensor import Dev\n" +
            "buf = bytearray([0])\n" +
            "d = Dev(2)\n" +
            "d.set_raw(5)\n" +
            "buf[0] = d.base\n");

        LastStored(ir, 0).Should().Be(new Constant(5),
            because: "self.raw = value inside an imported method must reach Field.__set__");
    }
}
