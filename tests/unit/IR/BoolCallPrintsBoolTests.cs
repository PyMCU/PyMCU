using FluentAssertions;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// `print()` of a call or property whose declared return type is `bool` must
/// spell True/False, the same as a comparison argument does. `print(d.value)`
/// on adafruit_debouncer -- where `value` is `@property -> bool` -- printed
/// 0/1 through the decimal writer before the bool check looked at declared
/// return types.
/// </summary>
public class BoolCallPrintsBoolTests
{
    private const string Prelude =
        "from pymcu.types import uint8\n\n" +
        "def uart_write_str(s: const[str]):\n" +
        "    pass\n" +
        "def uart_write_decimal_u8(v: uint8):\n" +
        "    pass\n" +
        "def uart_write_decimal_i16(v: int):\n" +
        "    pass\n";

    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(Prelude + src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    private static bool Calls(Instruction i, string name) =>
        i is Call c && c.FunctionName == name;

    [Fact]
    public void APropertyReturningBool_PrintsAsWordsNotDecimal()
    {
        var main = Gen(
            "class D:\n" +
            "    def __init__(self) -> None:\n" +
            "        self._s = 0\n" +
            "    @property\n" +
            "    def value(self) -> bool:\n" +
            "        return self._s != 0\n\n" +
            "d = D()\n" +
            "def main():\n" +
            "    print(d.value)\n").Functions.Single(f => f.Name == "main");

        main.Body.Any(i => Calls(i, "uart_write_decimal_u8")).Should().BeFalse(
            "a `-> bool` property must not reach the decimal writer");
        main.Body.Any(i => Calls(i, "uart_write_str")).Should().BeTrue(
            "EmitStreamBool writes the True/False flash words");
    }

    [Fact]
    public void AFunctionReturningBool_PrintsAsWordsNotDecimal()
    {
        var main = Gen(
            "def pred() -> bool:\n" +
            "    return False\n\n" +
            "def main():\n" +
            "    print(pred())\n").Functions.Single(f => f.Name == "main");

        main.Body.Any(i => Calls(i, "uart_write_decimal_u8")).Should().BeFalse();
        main.Body.Any(i => Calls(i, "uart_write_str")).Should().BeTrue();
    }

    [Fact]
    public void AnIntProperty_StillPrintsAsDecimal()
    {
        // GPIOR0.value seeds the field with a register read -- a runtime int,
        // not a compile-time fold, so the decimal writer must actually run.
        var main = Gen(
            "from pymcu.chips.atmega328p import GPIOR0\n" +
            "class D:\n" +
            "    def __init__(self) -> None:\n" +
            "        self._n = GPIOR0.value\n" +
            "    @property\n" +
            "    def count(self) -> int:\n" +
            "        return self._n\n\n" +
            "d = D()\n" +
            "def main():\n" +
            "    print(d.count)\n").Functions.Single(f => f.Name == "main");

        main.Body.Any(i => i is Call c && c.FunctionName.StartsWith("uart_write_decimal_"))
            .Should().BeTrue("an int property still formats as a number");
    }

    [Fact]
    public void APropertyWithGetterAndSetter_StillPrintsAsWords()
    {
        // The setter shares the getter's method name; registering its `-> None`
        // return under the same key overwrote the getter's `-> bool`, so
        // `print(d.value)` on adafruit_pcf8574's DigitalInOut went to the
        // decimal writer.
        var main = Gen(
            "class D:\n" +
            "    def __init__(self) -> None:\n" +
            "        self._s = 0\n" +
            "    @property\n" +
            "    def value(self) -> bool:\n" +
            "        return self._s != 0\n" +
            "    @value.setter\n" +
            "    def value(self, v: bool) -> None:\n" +
            "        self._s = 1 if v else 0\n\n" +
            "d = D()\n" +
            "def main():\n" +
            "    print(d.value)\n").Functions.Single(f => f.Name == "main");

        main.Body.Any(i => Calls(i, "uart_write_decimal_u8")).Should().BeFalse(
            "the setter's `-> None` must not shadow the getter's `-> bool`");
        main.Body.Any(i => Calls(i, "uart_write_str")).Should().BeTrue();
    }

    [Fact]
    public void IsinstanceFold_PrintsAsWordsNotDecimal()
    {
        // `isinstance` is a builtin: it has no functionReturnTypes entry, so
        // `print(isinstance(d, D))` reached the decimal writer and sent 1/0
        // where CPython spells True/False.
        var main = Gen(
            "class D:\n" +
            "    def __init__(self) -> None:\n" +
            "        self._s = 0\n\n" +
            "d = D()\n" +
            "def main():\n" +
            "    print(isinstance(d, D))\n").Functions.Single(f => f.Name == "main");

        main.Body.Any(i => Calls(i, "uart_write_decimal_u8")).Should().BeFalse(
            "isinstance() always yields a Python bool");
        main.Body.Any(i => Calls(i, "uart_write_str")).Should().BeTrue();
    }

    [Fact]
    public void AComparisonThroughAClassDunder_BindsWhatTheDunderReturns()
    {
        // `Cell.__eq__` returns `self.n + other.n` -- an int, 7 here, so `v = a == b`
        // holds 7 and CPython prints 7. The bool scan marked every comparison-shaped
        // binding bool before any class was registered; only a later pass can see the
        // dunder's return type and take the mark back (probe
        // 282_cmp_dunder_in_value_position).
        var main = Gen(
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
            "go()\n").Functions.Single(f => f.Name == "go");

        main.Body.Any(i => i is Call c && c.FunctionName.StartsWith("uart_write_decimal_"))
            .Should().BeTrue("__eq__ returned an int: v is a number, not a bool -- " +
                "a bool mark would have routed it to EmitStreamBool's True/False words");
    }

    [Fact]
    public void AComparisonThroughABoolDunder_KeepsItsBoolPrint()
    {
        // The demotion must not overreach: a class whose comparison dunder returns
        // only bools still binds a bool, and `v` prints True/False.
        var main = Gen(
            "class Cell:\n" +
            "    def __init__(self, n):\n" +
            "        self.n = n\n" +
            "    def __eq__(self, other):\n" +
            "        return self.n == other.n\n\n" +
            "def go():\n" +
            "    a = Cell(3)\n" +
            "    b = Cell(4)\n" +
            "    v = a == b\n" +
            "    print(v)\n" +
            "go()\n").Functions.Single(f => f.Name == "go");

        main.Body.Any(i => Calls(i, "uart_write_str")).Should().BeTrue(
            "__eq__ returning `self.n == other.n` returns a bool");
        main.Body.Any(i => i is Call c && c.FunctionName.StartsWith("uart_write_decimal_"))
            .Should().BeFalse();
    }
}
