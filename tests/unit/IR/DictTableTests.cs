using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU#332, #333, #334, #335, #336, #337. The six things a MicroPython 7-segment library
/// needs, each of which was refused on its own.
///
/// Measured against `github.com/kritishmohapatra/micropython-sevenseg`, which needed eight
/// edits to compile and now needs none.
/// </summary>
public class DictTableTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    private static List<FlashData> Tables(ProgramIR ir) =>
        ir.Functions.SelectMany(f => f.Body).OfType<FlashData>()
          .Where(t => t.Name.StartsWith("__cttab_")).ToList();

    private const string Part =
        "class Part:\n" +
        "    def __init__(self, n: uint8):\n" +
        "        self.n = n\n" +
        "    def get(self) -> uint8:\n" +
        "        return self.n\n\n";

    // ------------------------------------------------------- #332 comprehension

    [Fact]
    public void AComprehensionOfInstancesOverAParameter_BuildsTheSequence()
        => Assert.NotNull(Gen(Part +
            "class Bar:\n" +
            "    def __init__(self, ns):\n" +
            "        self.parts = [Part(n) for n in ns]\n" +
            "    def first(self) -> uint8:\n" +
            "        return self.parts[0].get()\n" +
            "def main():\n" +
            "    b = Bar([3, 5, 7])\n" +
            "    x = b.first()\n"));

    [Fact]
    public void AComprehensionOfInstancesOverARange_BuildsTheSequence()
        => Assert.NotNull(Gen(Part +
            "class Bar:\n" +
            "    def __init__(self):\n" +
            "        self.parts = [Part(i) for i in range(3)]\n" +
            "    def total(self) -> uint8:\n" +
            "        t: uint8 = 0\n" +
            "        for p in self.parts:\n" +
            "            t = t + p.get()\n" +
            "        return t\n" +
            "def main():\n" +
            "    b = Bar()\n" +
            "    x = b.total()\n"));

    // A comprehension whose length is decided at run time still has no array to fill.
    [Fact]
    public void AComprehensionOverARunTimeIterable_IsStillRefused()
        => Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(Part +
            "class Bar:\n" +
            "    def __init__(self, n: uint8):\n" +
            "        self.parts = [Part(i) for i in range(n)]\n" +
            "def main():\n" +
            "    b = Bar(3)\n"));

    // --------------------------------------------------------------- #335 dict field

    [Fact]
    public void ADictInAField_IsIndexedAndTestedAndCounted()
        => Assert.NotNull(Gen(
            "class Bar:\n" +
            "    def __init__(self):\n" +
            "        self.d = {0: 10, 1: 20, 2: 30}\n" +
            "    def at(self, n: uint8) -> uint8:\n" +
            "        if n not in self.d:\n" +
            "            return 0\n" +
            "        return self.d[n]\n" +
            "    def count(self) -> uint8:\n" +
            "        return len(self.d)\n" +
            "def main():\n" +
            "    b = Bar()\n" +
            "    x = b.at(1)\n" +
            "    y = b.count()\n"));

    // ---------------------------------------------------------- #336 dict of rows

    [Fact]
    public void ADictOfRowsReadWithARunTimeKey_GoesToFlashOnce()
    {
        var tables = Tables(Gen(
            "D = {0: [1, 2], 1: [3, 4], 2: [5, 6]}\n" +
            "def main():\n" +
            "    i: uint8 = 0\n" +
            "    while i < 3:\n" +
            "        row = D[i]\n" +
            "        a = row[0]\n" +
            "        i = i + 1\n"));
        Assert.Single(tables);
        Assert.Equal(new List<int> { 1, 2, 3, 4, 5, 6 }, tables[0].Bytes);
    }

    // A constant key is the row itself, and costs nothing.
    [Fact]
    public void ADictOfRowsReadWithAConstantKey_EmitsNoTable()
        => Assert.Empty(Tables(Gen(
            "D = {0: [1, 2], 1: [3, 4]}\n" +
            "def main():\n" +
            "    row = D[1]\n" +
            "    a = row[0]\n")));

    // Rows of different lengths are not a rectangle and have no table.
    [Fact]
    public void ADictOfRaggedRows_IsRefused()
        => Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(
            "D = {0: [1, 2], 1: [3, 4, 5]}\n" +
            "def main():\n" +
            "    i: uint8 = 0\n" +
            "    while i < 2:\n" +
            "        row = D[i]\n" +
            "        i = i + 1\n"));

    // Both subscripts written together, which is the only place the pair is visible: a row is
    // never a value, so `D[k][j]` had nowhere to put the row and tried to evaluate the list.
    [Fact]
    public void BothSubscriptsTogether_WithConstantKeys_FoldToTheElement()
        => Assert.Empty(Tables(Gen(
            "D = {0: [1, 2], 1: [3, 4]}\n" +
            "def main():\n" +
            "    a = D[0][1]\n")));

    [Fact]
    public void BothSubscriptsTogether_WithARunTimeKey_ReadsTheTable()
    {
        var tables = Tables(Gen(
            "D = {0: [1, 2], 1: [3, 4]}\n" +
            "def main():\n" +
            "    i: uint8 = 0\n" +
            "    while i < 2:\n" +
            "        a = D[i][1]\n" +
            "        i = i + 1\n"));
        Assert.Single(tables);
        Assert.Equal(new List<int> { 1, 2, 3, 4 }, tables[0].Bytes);
    }

    // ---------------------------------------------------------------- #337 zip

    [Fact]
    public void ZipWalksAFieldSequenceAgainstADictRow()
        => Assert.NotNull(Gen(Part +
            "class Bar:\n" +
            "    def __init__(self):\n" +
            "        self.parts = [Part(1), Part(2)]\n" +
            "        self.d = {0: [1, 0], 1: [0, 1]}\n" +
            "    def show(self, n: uint8) -> uint8:\n" +
            "        t: uint8 = 0\n" +
            "        row = self.d[n]\n" +
            "        for p, on in zip(self.parts, row):\n" +
            "            t = t + p.get() + on\n" +
            "        return t\n" +
            "def main():\n" +
            "    b = Bar()\n" +
            "    x = b.show(1)\n"));
}
