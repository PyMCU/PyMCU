using System.Linq;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// P2 AVR gaps bundle, item 7. Found while adding a `bytearray` overload to
/// `pymcu.hal.avr.uart.UART.write` (alongside its existing `uint8` one): an UNRELATED
/// program broke -- `uart.write(buf[j])` (a scalar ELEMENT read, runtime index) silently
/// selected the bytearray overload instead of the uint8 one, and failed compiling that
/// overload's body ("len() argument must be a fixed-size array or list literal") for a
/// value that was never an array.
///
/// Root cause, in ResolveOverloadedCallee's ArgTypeSuffix (Call.cs): the "is this argument
/// a variable-indexed (SRAM) array, so the bytearray overload should be picked" check tries
/// THREE keys -- the alias-chased `key` (properly scoped to the current inline expansion via
/// currentInlinePrefix), and a fallback `qKey = currentFunction + "." + <bare name>`. qKey is
/// a genuinely useful fallback OUTSIDE any inline expansion (where currentInlinePrefix is
/// empty and key degrades to the bare name itself), but INSIDE one it stops being a
/// fallback: currentFunction still names the OUTERMOST function, so qKey becomes "<outer
/// function>.<bare parameter name>" -- a coincidence, not a resolution, whenever an
/// UNRELATED variable in the outer scope happens to share that bare name. An @inline method
/// forwarding its OWN same-named parameter into a call one level down (the MicroPython
/// compat layer's machine.UART.write(uint8) forwards its "buf" parameter to
/// self._hw.write(buf)) reads qKey = "main.buf" whenever the ORIGINAL caller also happened
/// to have a local array named "buf" -- which it does here on purpose, to reproduce it.
///
/// Fixed by only trusting qKey outside an inline expansion; inside one, `key` (correctly
/// scoped) is what to trust, and qKey degrades to an alias of it rather than a separate,
/// coincidence-prone guess.
/// </summary>
public class OverloadArgTypeNameCollisionTests
{
    private const string Prelude =
        "from pymcu.chips.atmega328p import GPIOR0\n" +
        "from pymcu.types import uint8, inline\n" +
        "def uart_write_decimal_u8(v: uint8):\n" +
        "    pass\n" +
        "def uart_write_decimal_u16(v: uint8):\n" +
        "    pass\n" +
        "\n" +
        "class Inner:\n" +
        "    @inline\n" +
        "    def w(self, data: uint8):\n" +
        "        uart_write_decimal_u8(data)\n" +
        "    @inline\n" +
        "    def w(self, data: bytearray):\n" +
        "        uart_write_decimal_u16(len(data))\n" +
        "\n" +
        "class Outer:\n" +
        "    @inline\n" +
        "    def __init__(self):\n" +
        "        self.inner = Inner()\n" +
        "    @inline\n" +
        "    def forward(self, buf: uint8):\n" +
        "        self.inner.w(buf)\n" +
        "\n" +
        "def main() -> None:\n";

    private static ProgramIR Gen(string body) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(Prelude + body).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });

    [Fact]
    public void AScalarElementReadForwardedThroughASameNamedParameter_PicksTheScalarOverload()
    {
        var ir = Gen(
            "    o = Outer()\n" +
            "    buf: uint8[3] = [1, 2, 3]\n" +
            "    a: uint8 = GPIOR0.value\n" +
            "    o.forward(buf[a])\n");
        var calls = ir.Functions.SelectMany(f => f.Body).OfType<Call>().Select(c => c.FunctionName).ToList();
        Assert.Contains(calls, n => n.Contains("uart_write_decimal_u8"));
        Assert.DoesNotContain(calls, n => n.Contains("uart_write_decimal_u16"));
    }

    [Fact]
    public void PassingTheWholeArrayItself_StillPicksTheBytearrayOverload()
    {
        // The fix must not blind the SRAM-array detection outright: passing the array BY
        // NAME (not indexed) must still resolve to the bytearray overload.
        var ir = Gen(
            "    o = Outer()\n" +
            "    buf: uint8[3] = [1, 2, 3]\n" +
            "    a: uint8 = GPIOR0.value\n" +
            "    j: uint8 = buf[a]\n" +   // force buf into arraysWithVariableIndex
            "    o.inner.w(buf)\n");
        var calls = ir.Functions.SelectMany(f => f.Body).OfType<Call>().Select(c => c.FunctionName).ToList();
        Assert.Contains(calls, n => n.Contains("uart_write_decimal_u16"));
    }
}
