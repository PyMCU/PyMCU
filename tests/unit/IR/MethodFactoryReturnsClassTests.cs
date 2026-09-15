using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU#421. A method call that returns a class instance -- `led = pcf.get_pin(7)`, an
/// ORDINARY method, not a constructor, whose body builds and returns a `DigitalInOut` --
/// never tagged the assignment target with a class, so the next method call on it
/// (`led.switch_to_output(...)`) mangled to an undefined `led_switch_to_output`. Confirmed
/// this is about the receiver's missing class, not the keyword argument: the same call fails
/// identically written positionally, and even fails chained with no assignment at all.
///
/// A second, narrower bug found while reducing this: the analogous single-field "factory
/// handle" tracking (a bare, non-@inline free function returning a single-field class)
/// qualified the assignment target as `currentFunction + "." + name` instead of
/// `SlotInstanceKey(name)` -- the same mismatch #390 fixed for `with`-bound names.
/// </summary>
public class MethodFactoryReturnsClassTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });

    private const string Preamble =
        "from pymcu.chips.atmega328p import GPIOR0, GPIOR1\n" +
        "from pymcu.types import uint8\n\n\n";

    [Fact]
    public void AModuleLevelSingleFieldFreeFunctionFactoryTargetDispatches()
    {
        // The narrower, single-field bug: a non-@inline free function factory, called at
        // module level (so its target is a topLevelInstanceTarget under a bare name, not
        // "main."-qualified). It used to raise UserError("call to undefined function
        // 'led_value' ...").
        var ir = Gen(Preamble +
            "class Pin:\n" +
            "    def __init__(self, n: uint8) -> None:\n" +
            "        self._n: uint8 = n\n\n" +
            "    def value(self, v: uint8) -> None:\n" +
            "        GPIOR1.value = self._n + v\n\n" +
            "def get_pin(n: uint8) -> Pin:\n" +
            "    return Pin(n)\n\n" +
            "led = get_pin(7)\n" +
            "led.value(1)\n");

        Assert.Contains(ir.Functions, f => f.Name == "main");
    }

    [Fact]
    public void AMethodThatReturnsAConstructedInstanceTagsItsAssignmentTarget()
    {
        // The reported shape: adafruit_pcf8574's `pcf.get_pin(pin)` reduced -- an ORDINARY
        // method (not a constructor) whose body builds and returns a multi-field instance
        // of a DIFFERENT class. It used to raise UserError("call to undefined function
        // 'led_switch_to_output' ...").
        var ir = Gen(Preamble +
            "class Pin:\n" +
            "    def __init__(self, n: uint8, owner: \"Owner\") -> None:\n" +
            "        self._n: uint8 = n\n" +
            "        self._owner: Owner = owner\n\n" +
            "    def switch_to_output(self, value: bool = False) -> None:\n" +
            "        self._owner.written = value\n\n" +
            "class Owner:\n" +
            "    def __init__(self) -> None:\n" +
            "        self.written: bool = False\n\n" +
            "    def get_pin(self, n: uint8) -> Pin:\n" +
            "        return Pin(n, self)\n\n" +
            "o = Owner()\n" +
            "led = o.get_pin(7)\n" +
            "led.switch_to_output(True)\n" +
            "GPIOR1.value = o.written\n");

        // Not just "it builds" -- the read has to reach the SAME storage the write went to
        // (o_written), not an untagged, never-written name.
        var stores = ir.Functions.SelectMany(f => f.Body).OfType<Copy>()
            .Where(c => c.Dst is Variable v && v.Name.EndsWith("GPIOR1", StringComparison.Ordinal))
            .ToList();
        Assert.Contains(stores, s => s.Src is Variable sv && sv.Name == "o_written");
    }

    [Fact]
    public void TheKeywordArgumentIsNotWhatMakesItWork()
    {
        // Same as the reported shape, but with the exact keyword-argument spelling the
        // issue is filed against (value=True, plus **kwargs on the signature): confirms the
        // fix is not specific to the positional form used in the test above.
        var ir = Gen(Preamble +
            "class Pin:\n" +
            "    def __init__(self, n: uint8, owner: \"Owner\") -> None:\n" +
            "        self._n: uint8 = n\n" +
            "        self._owner: Owner = owner\n\n" +
            "    def switch_to_output(self, value: bool = False, **kwargs) -> None:\n" +
            "        self._owner.written = value\n\n" +
            "class Owner:\n" +
            "    def __init__(self) -> None:\n" +
            "        self.written: bool = False\n\n" +
            "    def get_pin(self, n: uint8) -> Pin:\n" +
            "        return Pin(n, self)\n\n" +
            "o = Owner()\n" +
            "led = o.get_pin(7)\n" +
            "led.switch_to_output(value=True)\n" +
            "GPIOR1.value = o.written\n");

        var stores = ir.Functions.SelectMany(f => f.Body).OfType<Copy>()
            .Where(c => c.Dst is Variable v && v.Name.EndsWith("GPIOR1", StringComparison.Ordinal))
            .ToList();
        Assert.Contains(stores, s => s.Src is Variable sv && sv.Name == "o_written");
    }
}
