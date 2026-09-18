using System.Linq;
using FluentAssertions;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// An IndexExpr unpack stores each unpacked value at its index. Adafruit
/// sht31d writes
/// <c>word[i*2], crc[i*2], word[(i*2)+1], crc[(i*2)+1] = struct.unpack(...)</c>.
/// </summary>
public class IndexedUnpackIRTests
{
    private static ProgramIR Gen(string src)
    {
        var ir = new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });
        return ir;
    }

    private static IEnumerable<Instruction> Body(ProgramIR ir) =>
        ir.Functions.SelectMany(f => f.Body);

    [Fact]
    public void FourSubscriptTargets_StoreEachUnpackedValue()
    {
        var ir = Gen(
            "from pymcu.types import uint8, uint16\n" +
            "def main() -> uint8:\n" +
            "    word: uint16[4] = [0, 0, 0, 0]\n" +
            "    crc: uint8[4] = [0, 0, 0, 0]\n" +
            "    word[0], crc[0], word[1], crc[1] = 0x12, 0xAB, 0x34, 0xCD\n" +
            "    return crc[0]\n");

        var stored = Body(ir).OfType<Copy>().Select(c => c.Src)
            .Concat(Body(ir).OfType<ArrayStore>().Select(s => s.Src)).ToList();
        stored.Should().Contain(new Constant(0x12),
            because: "word[0] receives the first unpacked value 0x12");
        stored.Should().Contain(new Constant(0xAB),
            because: "crc[0] receives the second unpacked value 0xAB");
        stored.Should().Contain(new Constant(0xCD),
            because: "crc[1] receives the fourth unpacked value 0xCD");
    }

    [Fact]
    public void StructUnpackIntoSubscripts_Compiles()
    {
        var ir = Gen(
            "from pymcu.types import uint8, uint16\n" +
            "import struct\n" +
            "def main() -> uint8:\n" +
            "    data: uint8[6] = [0x12, 0x34, 0xAB, 0x56, 0x78, 0xCD]\n" +
            "    word: uint16[4] = [0, 0, 0, 0]\n" +
            "    crc: uint8[4] = [0, 0, 0, 0]\n" +
            "    word[0], crc[0], word[1], crc[1] = struct.unpack(\">HBHB\", data)\n" +
            "    return crc[0]\n");
        ir.Should().NotBeNull(
            because: "struct.unpack into word[i], crc[i] is t = unpack(...) plus indexed stores");
        Body(ir).Should().NotBeEmpty(
            because: "the four unpacked fields must be lowered into the two arrays");
    }

    [Fact]
    public void TheSht31dRuntimeIndexSpelling_Compiles()
    {
        var ir = Gen(
            "from pymcu.types import uint8, uint16\n" +
            "import struct\n" +
            "def main() -> uint8:\n" +
            "    data: uint8[6] = [0x12, 0x34, 0xAB, 0x56, 0x78, 0xCD]\n" +
            "    word: uint16[4] = [0, 0, 0, 0]\n" +
            "    crc: uint8[4] = [0, 0, 0, 0]\n" +
            "    i: uint8 = 0\n" +
            "    word[i * 2], crc[i * 2], word[(i * 2) + 1], crc[(i * 2) + 1] = struct.unpack(\n" +
            "        \">HBHB\", data[i * 6 : (i * 6) + 6])\n" +
            "    return crc[0]\n");
        ir.Should().NotBeNull(
            because: "sht31d's word[i*2], crc[i*2], ... = struct.unpack(\">HBHB\", data[i*6:(i*6)+6]) must compile");
    }
}
