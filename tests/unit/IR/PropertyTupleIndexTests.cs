using FluentAssertions;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// <c>self.measurements[0]</c> on a tuple-returning @property. <c>f()[k]</c>
/// already allocated the slots; a property is a call and was visited as a
/// scalar ("returns 2 values; unpack them"). Adafruit sht4x writes
/// <c>return self.measurements[0]</c> from <c>temperature</c>.
/// </summary>
public class PropertyTupleIndexTests
{
    private static ProgramIR Gen(string src)
    {
        var ir = Optimizer.Optimize(new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" }));
        return ir;
    }

    private static ProgramIR GenImported(string sensor, string main)
    {
        var imported = new Dictionary<string, ProgramNode>
        {
            ["sensor"] = new Parser(new Lexer(sensor).Tokenize()).ParseProgram(),
        };
        return Optimizer.Optimize(new IRGenerator().Generate(
            new Parser(new Lexer(main).Tokenize()).ParseProgram(),
            imported,
            new DeviceConfig { Arch = "avr" },
            projectModules: new HashSet<string> { "sensor" }));
    }

    private static Val LastStored(ProgramIR ir, int slot) =>
        ir.Functions.SelectMany(f => f.Body).OfType<ArrayStore>()
            .Where(s => s.Index is Constant k && k.Value == slot)
            .Select(s => s.Src).Last();

    private const string Sht =
        "from pymcu.types import uint8\n" +
        "class SHT:\n" +
        "    def __init__(self):\n" +
        "        pass\n" +
        "    @property\n" +
        "    def measurements(self) -> (uint8, uint8):\n" +
        "        return (10, 20)\n" +
        "    @property\n" +
        "    def temperature(self) -> uint8:\n" +
        "        return self.measurements[0]\n" +
        "    @property\n" +
        "    def humidity(self) -> uint8:\n" +
        "        return self.measurements[1]\n";

    [Fact]
    public void ATupleProperty_IndexedFromAnotherProperty_IsThatElement()
    {
        var ir = Gen(
            Sht +
            "buf = bytearray([0, 0])\n" +
            "s = SHT()\n" +
            "buf[0] = s.temperature\n" +
            "buf[1] = s.humidity\n");

        LastStored(ir, 0).Should().Be(new Constant(10),
            because: "self.measurements[0] inside temperature is the first returned slot");
        LastStored(ir, 1).Should().Be(new Constant(20),
            because: "self.measurements[1] inside humidity is the second returned slot");
    }

    [Fact]
    public void ATupleProperty_IndexedOnTheInstance_IsThatElement()
    {
        var ir = Gen(
            Sht +
            "buf = bytearray([0])\n" +
            "s = SHT()\n" +
            "buf[0] = s.measurements[0]\n");

        LastStored(ir, 0).Should().Be(new Constant(10),
            because: "s.measurements[0] is f()[k] on the getter call");
    }

    [Fact]
    public void ATupleProperty_OnAnImportedClass_IndexesTheSameWay()
    {
        var ir = GenImported(
            Sht,
            "from pymcu.types import uint8\n" +
            "from sensor import SHT\n" +
            "buf = bytearray([0])\n" +
            "s = SHT()\n" +
            "buf[0] = s.temperature\n");

        LastStored(ir, 0).Should().Be(new Constant(10),
            because: "an imported SHT.temperature is still measurements[0]");
    }
}
