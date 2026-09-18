using FluentAssertions;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// An unannotated field whose first store is a string literal was laid out as uint8,
/// so a later <c>str</c> write (adafruit_character_lcd's <c>self._message = ""</c>
/// then the <c>message</c> setter) was refused as numeric-vs-str. A first store of
/// <c>bytearray(...)</c> was the same shape for adafruit_74hc595's <c>_gpio</c>.
/// </summary>
public class StringFieldFromLiteralTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });

    private static string Refusal(string src) =>
        Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(src)).Message;

    [Fact]
    public void EmptyStringThenStrSetter_Compiles()
    {
        var ir = Gen(
            "from pymcu.types import uint8\n" +
            "class Lcd:\n" +
            "    def __init__(self):\n" +
            "        self._message = \"\"\n" +
            "    @property\n" +
            "    def message(self):\n" +
            "        return self._message\n" +
            "    @message.setter\n" +
            "    def message(self, message: str):\n" +
            "        self._message = message\n" +
            "buf = bytearray(1)\n" +
            "l = Lcd()\n" +
            "l.message = \"Hi\"\n" +
            "buf[0] = 1\n");

        ir.Functions.Should().NotBeEmpty(
            because: "self._message = '' is a str field, so the setter's str write is the same kind");
    }

    [Fact]
    public void BytearrayThenBufferSetter_Compiles()
    {
        var ir = Gen(
            "from pymcu.types import uint8\n" +
            "class Shift:\n" +
            "    def __init__(self, n: uint8):\n" +
            "        self._gpio = bytearray(n)\n" +
            "    @property\n" +
            "    def gpio(self):\n" +
            "        return self._gpio\n" +
            "    @gpio.setter\n" +
            "    def gpio(self, val: bytearray):\n" +
            "        self._gpio = val\n" +
            "buf = bytearray(1)\n" +
            "s = Shift(1)\n" +
            "buf[0] = 1\n");

        ir.Functions.Should().NotBeEmpty(
            because: "self._gpio = bytearray(n) is a buffer field, not a uint8 the setter then contradicts");
    }

    [Fact]
    public void BytearraySizedFromAFieldThatHoldsAConstant_Compiles()
    {
        var ir = Gen(
            "from pymcu.types import uint8\n" +
            "class Shift:\n" +
            "    def __init__(self, n: uint8):\n" +
            "        self._number_of_shift_registers = n\n" +
            "        self._gpio = bytearray(self._number_of_shift_registers)\n" +
            "buf = bytearray(1)\n" +
            "s = Shift(1)\n" +
            "buf[0] = 1\n");

        ir.Functions.Should().NotBeEmpty(
            because: "bytearray(self._n) after self._n = 1 is a compile-time size, adafruit_74hc595");
    }

    [Fact]
    public void IntThenStrStillRefused()
    {
        Refusal(
            "from pymcu.types import uint8\n" +
            "class Box:\n" +
            "    def __init__(self):\n" +
            "        self._x = 1\n" +
            "    def set(self, s: str):\n" +
            "        self._x = s\n" +
            "def main():\n" +
            "    b = Box()\n" +
            "    b.set(\"no\")\n")
            .Should().Contain("first typed as numeric",
                because: "an int store and a later str store still cannot share one field");
    }
}
