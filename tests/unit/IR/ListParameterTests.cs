using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU#313, #314, #315. A driver takes a list and keeps it: a list of instances (several
/// pins, several devices) or a list of numbers (thresholds, a table), handed to a constructor
/// or to a method and stored in a `self` field.
///
/// Before this, every one of those spellings either was refused or built clean and read zero:
/// the field became a scalar nothing had written, and the list argument stayed raw AST that
/// each subscript re-evaluated, so `ps[0]` built a SECOND pin and the write through it went
/// somewhere nobody could read back.
///
/// The shape is the one `objs = [A(1), A(2)]` already had at module level: a base key whose
/// elements live at `base__0`, `base__1`, .. with the length in arraySizes. A parameter and a
/// field are other NAMES for that base, never copies of it.
/// </summary>
public class ListParameterTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    private const string Part =
        "class Part:\n" +
        "    def __init__(self, n: uint8):\n" +
        "        self.n = n\n" +
        "    def get(self) -> uint8:\n" +
        "        return self.n\n\n";

    // ---------------------------------------------------------------- instances

    [Fact]
    public void AListOfInstancesInAField_IsIndexableByConstant()
        => Assert.NotNull(Gen(Part +
            "class Bar:\n" +
            "    def __init__(self, parts):\n" +
            "        self._parts = parts\n" +
            "    def first(self) -> uint8:\n" +
            "        return self._parts[0].get()\n" +
            "def main():\n" +
            "    b = Bar([Part(1), Part(2)])\n" +
            "    x = b.first()\n"));

    [Fact]
    public void AListOfInstancesInAField_Unrolls()
        => Assert.NotNull(Gen(Part +
            "class Bar:\n" +
            "    def __init__(self, parts):\n" +
            "        self._parts = parts\n" +
            "    def total(self) -> uint8:\n" +
            "        t: uint8 = 0\n" +
            "        for p in self._parts:\n" +
            "            t = t + p.get()\n" +
            "        return t\n" +
            "def main():\n" +
            "    b = Bar([Part(1), Part(2)])\n" +
            "    x = b.total()\n"));

    // The list may be built first and passed by name. Through a name it used to reach the
    // field as a scalar and every read answered zero.
    [Fact]
    public void AListOfInstancesPassedByName_ReachesTheField()
        => Assert.NotNull(Gen(Part +
            "class Bar:\n" +
            "    def __init__(self, parts):\n" +
            "        self._parts = parts\n" +
            "    def first(self) -> uint8:\n" +
            "        return self._parts[0].get()\n" +
            "parts = [Part(1), Part(2)]\n" +
            "def main():\n" +
            "    b = Bar(parts)\n" +
            "    x = b.first()\n"));

    // A METHOD parameter, not only the constructor.
    [Fact]
    public void AListOfInstancesGivenToAMethod_Unrolls()
        => Assert.NotNull(Gen(Part +
            "class Bar:\n" +
            "    def __init__(self, k: uint8):\n" +
            "        self.k = k\n" +
            "    def total(self, parts) -> uint8:\n" +
            "        t: uint8 = 0\n" +
            "        for p in parts:\n" +
            "            t = t + p.get()\n" +
            "        return t\n" +
            "def main():\n" +
            "    b = Bar(1)\n" +
            "    x = b.total([Part(1), Part(2)])\n"));

    // Two instances of the same class, each with its own list.
    [Fact]
    public void TwoDriversWithDifferentLists_KeepTheirOwn()
        => Assert.NotNull(Gen(Part +
            "class Bar:\n" +
            "    def __init__(self, parts):\n" +
            "        self._parts = parts\n" +
            "    def first(self) -> uint8:\n" +
            "        return self._parts[0].get()\n" +
            "def main():\n" +
            "    a = Bar([Part(1), Part(2)])\n" +
            "    b = Bar([Part(3), Part(4)])\n" +
            "    x = a.first()\n" +
            "    y = b.first()\n"));

    // A class holding a list of instances of a class that itself holds one.
    [Fact]
    public void ANestedListOfInstances_Unrolls()
        => Assert.NotNull(Gen(Part +
            "class Wrap:\n" +
            "    def __init__(self, part):\n" +
            "        self._part = part\n" +
            "    def get(self) -> uint8:\n" +
            "        return self._part.get()\n" +
            "class Bar:\n" +
            "    def __init__(self, wraps):\n" +
            "        self._wraps = wraps\n" +
            "    def total(self) -> uint8:\n" +
            "        t: uint8 = 0\n" +
            "        for w in self._wraps:\n" +
            "            t = t + w.get()\n" +
            "        return t\n" +
            "def main():\n" +
            "    b = Bar([Wrap(Part(1)), Wrap(Part(2))])\n" +
            "    x = b.total()\n"));

    // More than the eight-element unroll limit: the sequence is not a loop, so the limit
    // that caps a `for` over a constant range does not cap it.
    [Fact]
    public void MoreThanEightInstances_StillBind()
        => Assert.NotNull(Gen(Part +
            "class Bar:\n" +
            "    def __init__(self, parts):\n" +
            "        self._parts = parts\n" +
            "    def count(self) -> uint8:\n" +
            "        return len(self._parts)\n" +
            "def main():\n" +
            "    b = Bar([Part(1), Part(2), Part(3), Part(4), Part(5),\n" +
            "             Part(6), Part(7), Part(8), Part(9), Part(10)])\n" +
            "    x = b.count()\n"));

    // The instances have no run-time storage, so a run-time subscript has nothing to index.
    // Refused where it is written, naming the two things that do work.
    [Fact]
    public void ARunTimeSubscriptOfInstances_IsRefusedByName()
    {
        var ex = Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(Part +
            "class Bar:\n" +
            "    def __init__(self, parts):\n" +
            "        self._parts = parts\n" +
            "    def walk(self) -> uint8:\n" +
            "        i: uint8 = 0\n" +
            "        t: uint8 = 0\n" +
            "        while i < 2:\n" +
            "            t = t + self._parts[i].get()\n" +
            "            i = i + 1\n" +
            "        return t\n" +
            "def main():\n" +
            "    b = Bar([Part(1), Part(2)])\n" +
            "    x = b.walk()\n"));

        Assert.Contains("self._parts", ex.Message);
        Assert.Contains("compile-time instances", ex.Message);
        Assert.Contains("for p in self._parts", ex.Message);
    }

    // ------------------------------------------------------------------ numbers

    [Fact]
    public void AListOfNumbersInAField_FoldsAConstantSubscript()
        => Assert.NotNull(Gen(
            "class Bar:\n" +
            "    def __init__(self, levels):\n" +
            "        self._levels = levels\n" +
            "    def second(self) -> uint8:\n" +
            "        return self._levels[1]\n" +
            "def main():\n" +
            "    b = Bar([10, 20, 30])\n" +
            "    x = b.second()\n"));

    [Fact]
    public void AListOfNumbersInAField_Unrolls()
        => Assert.NotNull(Gen(
            "class Bar:\n" +
            "    def __init__(self, levels):\n" +
            "        self._levels = levels\n" +
            "    def total(self) -> uint8:\n" +
            "        t: uint8 = 0\n" +
            "        for v in self._levels:\n" +
            "            t = t + v\n" +
            "        return t\n" +
            "def main():\n" +
            "    b = Bar([10, 20, 30])\n" +
            "    x = b.total()\n"));

    [Fact]
    public void LenOfAListOfNumbersInAField_IsTheCount()
        => Assert.NotNull(Gen(
            "class Bar:\n" +
            "    def __init__(self, levels):\n" +
            "        self._levels = levels\n" +
            "    def count(self) -> uint8:\n" +
            "        return len(self._levels)\n" +
            "def main():\n" +
            "    b = Bar([10, 20, 30, 40])\n" +
            "    x = b.count()\n"));

    [Fact]
    public void AListOfNumbersPassedByName_ReachesTheField()
        => Assert.NotNull(Gen(
            "class Bar:\n" +
            "    def __init__(self, levels):\n" +
            "        self._levels = levels\n" +
            "    def second(self) -> uint8:\n" +
            "        return self._levels[1]\n" +
            "levels = [7, 8, 9]\n" +
            "def main():\n" +
            "    b = Bar(levels)\n" +
            "    x = b.second()\n"));

    // Compile-time values have no storage, so a run-time subscript is refused -- and told
    // which declaration gives the field storage that IS indexable at run time.
    [Fact]
    public void ARunTimeSubscriptOfNumbers_NamesTheDeclarationThatWouldWork()
    {
        var ex = Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(
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
            "    x = b.walk()\n"));

        Assert.Contains("self._levels", ex.Message);
        Assert.Contains("uint8[3]", ex.Message);
    }

    // ---------------------------------------------------------------- bytearray

    // A buffer handed to a driver stays the SAME buffer: the field is another name for it,
    // not a copy of its address into a scalar that the subscript then read as a bit index.
    [Fact]
    public void ABytearrayInAField_IsIndexableAtRunTime()
        => Assert.NotNull(Gen(
            "class Bar:\n" +
            "    def __init__(self, data):\n" +
            "        self._data = data\n" +
            "    def fill(self, v: uint8):\n" +
            "        i: uint8 = 0\n" +
            "        while i < 4:\n" +
            "            self._data[i] = v\n" +
            "            i = i + 1\n" +
            "buf = bytearray(4)\n" +
            "def main():\n" +
            "    b = Bar(buf)\n" +
            "    b.fill(7)\n"));

    // ------------------------------------------------------------------ scoping

    // A member assignment whose receiver is a chip REGISTER is not a field: writing a byte
    // to UDR0 from a parameter that shares its name with an array elsewhere must stay a
    // register write. This is the shape that made the whole AVR UART stop emitting.
    [Fact]
    public void ARegisterWriteFromASameNamedParameter_StaysARegisterWrite()
        => Assert.NotNull(Gen(
            "from pymcu.types import uint8, inline\n" +
            "from pymcu.chips.atmega328p import UDR0\n" +
            "@inline\n" +
            "def put(data: uint8):\n" +
            "    UDR0.value = data\n" +
            "def main():\n" +
            "    data: uint8[4] = [1, 2, 3, 4]\n" +
            "    put(data[0])\n"));
}
