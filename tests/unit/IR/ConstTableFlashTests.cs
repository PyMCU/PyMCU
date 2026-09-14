using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU#317. A lookup table written as a plain list and read with an index only known at run
/// time -- `DIGITS = [0x3F, 0x06, ...]` then `DIGITS[digit]` -- is the shape of every
/// 7-segment table, font and gamma curve in CircuitPython and MicroPython alike.
///
/// It was refused: an all-constant list lives as separate variables, which is free and exactly
/// right for a constant subscript and has nothing to index at run time, and the reader was told
/// to write `DIGITS: uint8[10] = [...]`, an annotation the idiom does not have.
///
/// The values are constants and nothing writes them, so the table goes to FLASH -- materialised
/// at the first run-time subscript that needs it, which is what keeps a table only ever indexed
/// with a constant at zero cost.
/// </summary>
public class ConstTableFlashTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    private static List<FlashData> Tables(ProgramIR ir) =>
        ir.Functions.SelectMany(f => f.Body).OfType<FlashData>()
          .Where(t => t.Name.StartsWith("__cttab_")).ToList();

    private const string RuntimeIndex =
        "DIGITS = [0x3F, 0x06, 0x5B, 0x4F, 0x66, 0x6D, 0x7D, 0x07, 0x7F, 0x6F]\n" +
        "def show(d: uint8) -> uint8:\n" +
        "    return DIGITS[d]\n" +
        "def main():\n" +
        "    i: uint8 = 0\n" +
        "    while i < 10:\n" +
        "        x = show(i)\n" +
        "        i = i + 1\n";

    [Fact]
    public void ATableReadWithARunTimeIndex_GoesToFlash()
    {
        var tables = Tables(Gen(RuntimeIndex));
        Assert.Single(tables);
        Assert.Equal(new List<int> { 0x3F, 0x06, 0x5B, 0x4F, 0x66, 0x6D, 0x7D, 0x07, 0x7F, 0x6F },
                     tables[0].Bytes);
    }

    // The whole point of materialising on demand: a table nobody indexes at run time is still
    // separate constants, and emits nothing.
    [Fact]
    public void ATableReadOnlyWithConstants_EmitsNoTable()
        => Assert.Empty(Tables(Gen(
            "DIGITS = [0x3F, 0x06, 0x5B, 0x4F, 0x66, 0x6D, 0x7D, 0x07, 0x7F, 0x6F]\n" +
            "def main():\n" +
            "    a = DIGITS[0]\n" +
            "    b = DIGITS[9]\n")));

    // Flash cannot be written, so a table the program writes keeps the behaviour it had: it is
    // refused, and told how to get storage that is indexable at run time.
    [Fact]
    public void ATableTheProgramWrites_IsStillRefused()
    {
        var ex = Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(
            "T = [1, 2, 3, 4]\n" +
            "def main():\n" +
            "    T[0] = 9\n" +
            "    i: uint8 = 0\n" +
            "    while i < 4:\n" +
            "        x = T[i]\n" +
            "        i = i + 1\n"));
        Assert.Contains("not addressable at run time", ex.Message);
    }

    // The widest element decides the width, and the element is stored little-endian, which is
    // what the flash reader already knows how to reassemble.
    [Fact]
    public void AWideTable_KeepsItsWidth()
    {
        var tables = Tables(Gen(
            "DUTIES = [256, 383, 512, 640]\n" +
            "def main():\n" +
            "    i: uint8 = 0\n" +
            "    while i < 4:\n" +
            "        x = DUTIES[i]\n" +
            "        i = i + 1\n"));
        Assert.Single(tables);
        Assert.Equal(new List<int> { 0x00, 0x01, 0x7F, 0x01, 0x00, 0x02, 0x80, 0x02 },
                     tables[0].Bytes);
    }

    // Reached through a `self` field, which is where a driver keeps the table it was handed.
    [Fact]
    public void ATableHeldInAField_GoesToFlash()
        => Assert.Single(Tables(Gen(
            "class Bar:\n" +
            "    def __init__(self, levels):\n" +
            "        self._levels = levels\n" +
            "    def walk(self) -> uint8:\n" +
            "        i: uint8 = 0\n" +
            "        t: uint8 = 0\n" +
            "        while i < 3:\n" +
            "            t = t + self._levels[i]\n" +
            "            i = i + 1\n" +
            "        return t\n" +
            "def main():\n" +
            "    b = Bar([10, 20, 30])\n" +
            "    x = b.walk()\n")));

    // Reached through a parameter that was handed the literal directly.
    [Fact]
    public void ATableBoundToAParameter_GoesToFlash()
        => Assert.Single(Tables(Gen(
            "class Bar:\n" +
            "    def __init__(self, xs):\n" +
            "        i: uint8 = 0\n" +
            "        t: uint8 = 0\n" +
            "        while i < 3:\n" +
            "            t = t + xs[i]\n" +
            "            i = i + 1\n" +
            "        self.t = t\n" +
            "def main():\n" +
            "    b = Bar([10, 20, 30])\n")));

    // Two run-time subscripts of the same table share one copy of it.
    [Fact]
    public void TwoRunTimeReadsOfOneTable_ShareOneCopy()
        => Assert.Single(Tables(Gen(
            "T = [1, 2, 3, 4]\n" +
            "def main():\n" +
            "    i: uint8 = 0\n" +
            "    while i < 4:\n" +
            "        a = T[i]\n" +
            "        b = T[i]\n" +
            "        i = i + 1\n")));
}
