using FluentAssertions;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// The kind a name bound to a comparison takes (#386): unannotated it IS the
/// Python bool the comparison answers (`cmp = x > 1` prints True/False, and so
/// does `c = 300 in d` -- CPython wraps `bool(__contains__(...))` even when the
/// dunder returns an int). An integer annotation keeps the binding a number
/// (`c: uint8 = 300 in d` stores the byte and prints 1/0), a `bool` annotation
/// spells the words, and a comparison that provably dispatches a class dunder
/// returning non-bool binds what the dunder returned. A name re-stored with an
/// integer keeps printing digits everywhere (the bool veto is one-way).
/// </summary>
public class BoundComparisonNameTests
{
    private const string Prelude =
        "from pymcu.types import uint8, int16\n" +
        "def uart_write_str(s: const[str]):\n" +
        "    pass\n" +
        "def uart_write_decimal_u8(v: uint8):\n" +
        "    pass\n";

    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(Prelude + src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    private static bool WritesDecimal(Function f) =>
        f.Body.Any(i => i is Call c && c.FunctionName.StartsWith("uart_write_decimal_"));

    // Every text handed to the string writer -- a bool print streams the
    // True/False flash words, a number print only ever streams the newline.
    private static List<string> StrWrites(ProgramIR ir) =>
        ir.Functions.SelectMany(f => f.Body)
            .OfType<Copy>()
            .Where(c => c.Dst is Variable { Name: "uart_write_str.s" } && c.Src is FlashStrAddr)
            .Select(c => new string(ir.Functions.SelectMany(f => f.Body).OfType<FlashData>()
                .Single(fd => fd.Name == ((FlashStrAddr)c.Src).Name).Bytes
                .TakeWhile(b => b != 0).Select(b => (char)b).ToArray()))
            .ToList();

    private static bool WritesBoolWords(ProgramIR ir) =>
        StrWrites(ir).Any(t => t is "True" or "False");

    [Fact]
    public void AnUnannotatedComparisonName_IsABool()
    {
        // `cmp = x > 1` binds the bool CPython binds: print must stream the
        // True/False words, not the decimal digit (fstring-bool fixture).
        var ir = Gen(
            "from pymcu.chips.atmega328p import GPIOR0\n" +
            "def main():\n" +
            "    x: uint8 = GPIOR0.value\n" +
            "    cmp = x > 1\n" +
            "    print(cmp)\n");
        var main = ir.Functions.Single(f => f.Name == "main");

        WritesBoolWords(ir).Should().BeTrue(
            "an unannotated comparison binding is a bool, like CPython");
        WritesDecimal(main).Should().BeFalse(
            "no decimal writer for a bool name");
    }

    [Fact]
    public void AnUnannotatedMembershipName_IsABoolEvenThroughAnIntContains()
    {
        // CPython's `in` always yields bool: `3 in d` is `bool(d.__contains__
        // (3))`, True even though a FixedDict-style __contains__ returns uint8.
        var ir = Gen(
            "class Bag:\n" +
            "    def __init__(self, n: uint8) -> None:\n" +
            "        self._v = n\n" +
            "    def __contains__(self, k: uint8) -> uint8:\n" +
            "        return self._v\n\n" +
            "def main():\n" +
            "    d = Bag(3)\n" +
            "    c = 3 in d\n" +
            "    print(c)\n");
        var main = ir.Functions.Single(f => f.Name == "main");

        WritesBoolWords(ir).Should().BeTrue(
            "membership answers a bool whatever __contains__ returns");
        WritesDecimal(main).Should().BeFalse();
    }

    [Fact]
    public void AnIntegerAnnotatedMembershipName_StaysNumeric()
    {
        // The annotation is the binding's type in PyMCU's model: `c: uint8 =
        // 3 in d` stores the byte the dunder returned and prints 1/0
        // (fixeddict fixture).
        var ir = Gen(
            "class Bag:\n" +
            "    def __init__(self, n: uint8) -> None:\n" +
            "        self._v = n\n" +
            "    def __contains__(self, k: uint8) -> uint8:\n" +
            "        return self._v\n\n" +
            "def main():\n" +
            "    d = Bag(3)\n" +
            "    c: uint8 = 3 in d\n" +
            "    print(c)\n");
        var main = ir.Functions.Single(f => f.Name == "main");

        WritesDecimal(main).Should().BeTrue(
            "the uint8 annotation makes c a byte, not a bool");
        WritesBoolWords(ir).Should().BeFalse();
    }

    [Fact]
    public void AnIntegerAnnotatedComparisonName_StaysNumeric()
    {
        // `c: int16 = x > 1`: same annotation rule on a plain comparison.
        var ir = Gen(
            "from pymcu.chips.atmega328p import GPIOR0\n" +
            "def main():\n" +
            "    x: uint8 = GPIOR0.value\n" +
            "    c: int16 = x > 1\n" +
            "    print(c)\n");
        var main = ir.Functions.Single(f => f.Name == "main");

        WritesDecimal(main).Should().BeTrue(
            "the int16 annotation makes c a number, not a bool");
        WritesBoolWords(ir).Should().BeFalse();
    }

    [Fact]
    public void ABoolAnnotatedComparisonName_KeepsTheWords()
    {
        var ir = Gen(
            "from pymcu.chips.atmega328p import GPIOR0\n" +
            "def main():\n" +
            "    x: uint8 = GPIOR0.value\n" +
            "    c: bool = x > 1\n" +
            "    print(c)\n");
        var main = ir.Functions.Single(f => f.Name == "main");

        WritesBoolWords(ir).Should().BeTrue(
            "a bool annotation declares what the comparison already is");
        WritesDecimal(main).Should().BeFalse();
    }

    [Fact]
    public void ANameRestoredToAnInteger_StaysNumeric()
    {
        // `mixed = False; mixed = x - 3`: the second store vetoes the bool
        // mark, so the name prints digits at every read (fstring-bool fixture).
        var ir = Gen(
            "from pymcu.chips.atmega328p import GPIOR0\n" +
            "def main():\n" +
            "    x: uint8 = GPIOR0.value\n" +
            "    mixed = False\n" +
            "    print(mixed)\n" +
            "    mixed = x - 3\n" +
            "    print(mixed)\n");
        var main = ir.Functions.Single(f => f.Name == "main");

        WritesDecimal(main).Should().BeTrue(
            "one integer store vetoes the bool mark for every read of the name");
        WritesBoolWords(ir).Should().BeFalse(
            "even the first print spells the digit -- the veto is one-way");
    }

    [Fact]
    public void AModuleLevelComparisonName_IsABool()
    {
        // Same rule in the flat module namespace: `cmp = seed > 1` at top level
        // is a bool anywhere the program reads it.
        var ir = Gen(
            "from pymcu.chips.atmega328p import GPIOR0\n" +
            "seed: uint8 = GPIOR0.value\n" +
            "cmp = seed > 1\n" +
            "def main():\n" +
            "    print(cmp)\n");
        var main = ir.Functions.Single(f => f.Name == "main");

        WritesBoolWords(ir).Should().BeTrue();
        WritesDecimal(main).Should().BeFalse();
    }

    [Fact]
    public void AnAndOrOrNotName_IsABool()
    {
        // `and`/`or` of bools and `not` bind bools the same way comparisons do.
        var ir = Gen(
            "from pymcu.chips.atmega328p import GPIOR0\n" +
            "def main():\n" +
            "    x: uint8 = GPIOR0.value\n" +
            "    both = x > 1 and x < 9\n" +
            "    flip = not both\n" +
            "    print(both)\n" +
            "    print(flip)\n");
        var main = ir.Functions.Single(f => f.Name == "main");

        WritesBoolWords(ir).Should().BeTrue();
        WritesDecimal(main).Should().BeFalse();
    }

    [Fact]
    public void AComparisonThroughAnIntDunder_BindsWhatTheDunderReturns()
    {
        // `v = a == b` where Cell.__eq__ returns `self.n + other.n`: CPython's
        // `==` does not coerce to bool, so v holds the int 7 and prints digits
        // (probe 282_cmp_dunder_in_value_position).
        var ir = Gen(
            "class Cell:\n" +
            "    def __init__(self, n):\n" +
            "        self.n = n\n" +
            "    def __eq__(self, other):\n" +
            "        return self.n + other.n\n\n" +
            "def go():\n" +
            "    a = Cell(3)\n" +
            "    b = Cell(4)\n" +
            "    v = a == b\n" +
            "    print(v)\n" +
            "go()\n");
        var go = ir.Functions.Single(f => f.Name == "go");

        WritesDecimal(go).Should().BeTrue(
            "__eq__ returned an int: v is a number, not a bool");
        WritesBoolWords(ir).Should().BeFalse();
    }
}
