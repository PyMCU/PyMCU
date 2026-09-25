using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// `if REG[BIT]:` lowers to a single SBIS/SBIC when the bit index is known while compiling.
/// The resolver behind that only knew an integer literal and a bare module constant, so a
/// CLASS constant -- `if TIFR1[TIMER1.TOV1]:`, the spelling a grouped peripheral asks for
/// because it keeps its bit positions next to its registers -- missed it: the bit was
/// materialized into a register as 0 or 1 and then compared, ten bytes on AVR where the
/// direct test needs one instruction.
///
/// The assertions are on the jump the condition lowers to, not on sizes.
/// </summary>
public class ClassConstantBitIndexTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });

    private const string Header =
        "from pymcu.types import ptr, uint8\n" +
        "\n" +
        "TIFR1: ptr[uint8] = ptr(0x36)\n" +
        "\n" +
        "class TIMER1:\n" +
        "    TOV1: int = 0\n" +
        "    ICF1: int = 5\n" +
        "\n";

    private static List<Instruction> Code(ProgramIR ir) =>
        ir.Functions.SelectMany(f => f.Body).Where(i => i is not DebugLine).ToList();

    [Fact]
    public void AClassConstantBitIndex_LowersToTheDirectBitTest()
    {
        var ir = Gen(Header +
            "def main():\n" +
            "    if TIFR1[TIMER1.ICF1]:\n" +
            "        TIFR1[TIMER1.ICF1] = 1\n");

        Assert.Contains(Code(ir).OfType<JumpIfBitClear>(),
            j => j.Source is MemoryAddress { Address: 0x36 } && j.Bit == 5);
    }

    [Fact]
    public void AClassConstantBitIndexOfZero_IsStillAConstant()
    {
        // Bit 0 is the one a "did it resolve?" check written on the value rather than on
        // the presence of an answer would get wrong.
        var ir = Gen(Header +
            "def main():\n" +
            "    if TIFR1[TIMER1.TOV1]:\n" +
            "        TIFR1[TIMER1.TOV1] = 1\n");

        Assert.Contains(Code(ir).OfType<JumpIfBitClear>(),
            j => j.Source is MemoryAddress { Address: 0x36 } && j.Bit == 0);
    }

    [Fact]
    public void AClassConstantBitIndex_GeneratesWhatTheLiteralGenerates()
    {
        var named = Code(Gen(Header +
            "def main():\n" +
            "    if TIFR1[TIMER1.ICF1]:\n" +
            "        TIFR1[TIMER1.TOV1] = 1\n"));
        var literal = Code(Gen(Header +
            "def main():\n" +
            "    if TIFR1[5]:\n" +
            "        TIFR1[0] = 1\n"));

        Assert.NotEmpty(literal);
        Assert.Equal(
            literal.Select(i => i.ToString()).ToList(),
            named.Select(i => i.ToString()).ToList());
    }
}
