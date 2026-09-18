using FluentAssertions;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// <c>self._buffer[0:2]</c> is a slice of a field bytearray. A named
/// <c>buf[0:2]</c> already copied the window; the field is the same SRAM
/// array under a flattened name. Adafruit sht4x writes
/// <c>temp_data = self._buffer[0:2]</c> then CRC-checks that window.
/// </summary>
public class FieldBufferSliceTests
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

    private static IEnumerable<Instruction> Body(ProgramIR ir) =>
        ir.Functions.SelectMany(f => f.Body);

    private static void MustLoadFieldWindow(ProgramIR ir, int index, string because)
    {
        Body(ir).OfType<ArrayLoad>()
            .Where(l => l.ArrayName.Contains("_buffer") && l.Index is Constant k && k.Value == index)
            .Should().NotBeEmpty(because);
    }

    private const string Sensor =
        "from pymcu.types import uint8\n" +
        "class SHT:\n" +
        "    def __init__(self):\n" +
        "        self._buffer = bytearray([10, 20, 30, 40, 50, 60])\n" +
        "    def head(self) -> uint8:\n" +
        "        t = self._buffer[0:2]\n" +
        "        return t[0]\n" +
        "    def mid(self) -> uint8:\n" +
        "        t = self._buffer[3:5]\n" +
        "        return t[0]\n";

    [Fact]
    public void AFieldBytearraySlice_AssignedThenIndexed_IsThatWindow()
    {
        var ir = Gen(
            Sensor +
            "buf = bytearray([0, 0])\n" +
            "s = SHT()\n" +
            "buf[0] = s.head()\n" +
            "buf[1] = s.mid()\n");

        MustLoadFieldWindow(ir, 0,
            "self._buffer[0:2] copies the field array starting at byte 0");
        MustLoadFieldWindow(ir, 3,
            "self._buffer[3:5] copies the field array starting at byte 3");
    }

    [Fact]
    public void AFieldBytearraySlice_OnAnImportedClass_IsTheSameWindow()
    {
        var ir = GenImported(
            Sensor,
            "from pymcu.types import uint8\n" +
            "from sensor import SHT\n" +
            "buf = bytearray([0])\n" +
            "s = SHT()\n" +
            "buf[0] = s.head()\n");

        MustLoadFieldWindow(ir, 0,
            "an imported SHT._buffer[0:2] is still the field array window");
    }
}
