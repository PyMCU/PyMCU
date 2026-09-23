using FluentAssertions;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// <c>sr.gpio[0]</c> on adafruit_74hc595: <c>gpio</c> is a property returning
/// the object's buffer field. The expansion's ResultTemp aliases the member
/// array, but the subscript fell through to the scalar tail and emitted a
/// bit test of the alias -- <c>print(sr.gpio[0])</c> read bit 0 of the
/// storage name instead of element 0.
/// </summary>
public class PropertyBufferIndexTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });

    private const string Sr =
        "class SR:\n" +
        "    def __init__(self):\n" +
        "        self._gpio = bytearray(1)\n" +
        "    @property\n" +
        "    def gpio(self) -> bytearray:\n" +
        "        return self._gpio\n" +
        "    @gpio.setter\n" +
        "    def gpio(self, val: bytearray) -> None:\n" +
        "        self._gpio = val\n";

    [Fact]
    public void ABufferReturningProperty_IndexedAtTheCallSite_LoadsTheElement()
    {
        var ir = Gen(
            Sr +
            "g = bytearray([0xA5])\n" +
            "sr = SR()\n" +
            "sr.gpio = g\n" +
            "x = sr.gpio[0]\n");

        var main = ir.Functions.Single(f => f.Name == "main");
        main.Body.Any(i => i is ArrayLoad).Should().BeTrue(
            "the property result aliases the buffer, so the subscript is an element load");
        main.Body.Any(i => i is BitCheck).Should().BeFalse(
            "a bit test of the ResultTemp reads the alias, not the element");
    }

    [Fact]
    public void ABufferReturningProperty_OnANestedMember_LoadsTheElement()
    {
        // `self._shift_register.gpio[i]` inside DigitalInOut.value: the
        // receiver is itself a member access, so the f()[k] rewrite does not
        // fire and the ResultTemp reaches the generic subscript tail.
        var ir = Gen(
            Sr +
            "class Pin:\n" +
            "    def __init__(self, sr: SR):\n" +
            "        self._sr = sr\n" +
            "    @property\n" +
            "    def value(self) -> int:\n" +
            "        return self._sr.gpio[0]\n\n" +
            "sr = SR()\n" +
            "p = Pin(sr)\n" +
            "x = p.value\n");

        var main = ir.Functions.Single(f => f.Name == "main");
        main.Body.Any(i => i is ArrayLoad).Should().BeTrue(
            "the nested property result still aliases the member buffer");
        main.Body.Any(i => i is BitCheck).Should().BeFalse(
            "a bit test of the ResultTemp reads the alias, not the element");
    }
}
