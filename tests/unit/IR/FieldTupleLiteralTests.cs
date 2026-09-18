using FluentAssertions;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// A tuple of constants assigned to a field is a fixed array. Adafruit DPS310 writes
/// <c>self._oversample_scalefactor = (524288, 1572864, ..., 2088960)</c> in __init__
/// and later indexes it. That was refused as a runtime tuple.
/// </summary>
public class FieldTupleLiteralTests
{
    private static ProgramIR Gen(string src)
    {
        var ir = new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });
        return Optimizer.Optimize(ir);
    }

    private static ProgramIR GenImported(string sensor, string main)
    {
        var imported = new Dictionary<string, ProgramNode>
        {
            ["sensor"] = new Parser(new Lexer(sensor).Tokenize()).ParseProgram(),
        };
        var ir = new IRGenerator().Generate(
            new Parser(new Lexer(main).Tokenize()).ParseProgram(),
            imported,
            new DeviceConfig { Arch = "avr" },
            projectModules: new HashSet<string>(imported.Keys));
        return Optimizer.Optimize(ir);
    }

    private static IEnumerable<Instruction> Body(ProgramIR ir) =>
        ir.Functions.SelectMany(f => f.Body);

    private static bool StoredAt(ArrayStore s, int index, int value) =>
        s.Index is Constant k && k.Value == index && s.Src.Equals(new Constant(value));

    private static bool LoadedAt(ArrayLoad l, int index) =>
        l.Index is Constant k && k.Value == index;

    [Fact]
    public void AnUnannotatedTupleField_IsIndexableByConstant()
    {
        var ir = Gen(
            "buf = bytearray([0])\n" +
            "class Dev:\n" +
            "    def __init__(self):\n" +
            "        self.scale = (10, 20, 30)\n" +
            "    def at(self, n: uint8) -> uint8:\n" +
            "        return self.scale[n]\n" +
            "d = Dev()\n" +
            "buf[0] = d.at(1)\n");

        Body(ir).OfType<ArrayStore>().Should().Contain(s => StoredAt(s, 1, 20),
            because: "the tuple field is the array (10, 20, 30), so index 1 is 20");
        Body(ir).OfType<ArrayLoad>().Should().Contain(l => LoadedAt(l, 1),
            because: "scale[1] is an array load, not a bit check of a uint8 field");
        Body(ir).OfType<BitCheck>().Should().BeEmpty(
            because: "a tuple field is not a register to bit-index");
    }

    [Fact]
    public void AnEightElementUint32TupleField_FoldsThroughAnImportedMethod()
    {
        const string sensor =
            "class Dev:\n" +
            "    def __init__(self):\n" +
            "        self.scale = (\n" +
            "            524288,\n" +
            "            1572864,\n" +
            "            3670016,\n" +
            "            7864320,\n" +
            "            253952,\n" +
            "            516096,\n" +
            "            1040384,\n" +
            "            2088960,\n" +
            "        )\n" +
            "    def at(self, n: uint8) -> uint32:\n" +
            "        return self.scale[n]\n";
        var ir = GenImported(sensor,
            "from sensor import Dev\n" +
            "buf: uint32[1] = [0]\n" +
            "d = Dev()\n" +
            "buf[0] = d.at(6)\n");

        Body(ir).OfType<ArrayStore>().Should().Contain(s => StoredAt(s, 6, 1040384),
            because: "DPS310's table[6] is 1040384; a truncated uint16 would store 15360");
        Body(ir).OfType<ArrayLoad>().Should().Contain(l => LoadedAt(l, 6),
            because: "scale[6] is an array load of the uint32 table");
    }

    [Fact]
    public void ARuntimeIndexIntoATupleField_DoesNotAskForARuntimeTuple()
    {
        var act = () => Gen(
            "from pymcu.chips.atmega328p import GPIOR0\n" +
            "buf = bytearray([0])\n" +
            "class Dev:\n" +
            "    def __init__(self):\n" +
            "        self.scale = (10, 20, 30)\n" +
            "    def at(self, n: uint8) -> uint8:\n" +
            "        return self.scale[n]\n" +
            "d = Dev()\n" +
            "buf[0] = d.at(GPIOR0.value)\n");

        act.Should().NotThrow<PyMCU.Common.CompilerError>(
            because: "a run-time index into a tuple field is an array load, not a runtime tuple");
    }
}
