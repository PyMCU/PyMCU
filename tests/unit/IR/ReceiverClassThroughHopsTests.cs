using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU#318. A read of a field the receiver's class does not have is refused inside a
/// constructor, exactly as it is everywhere else.
///
/// The undefined-attribute check used to be scoped out of `__init__` entirely, because the
/// receiver's class could not be resolved reliably there: a PARAMETER answered with the class
/// under construction rather than with its own, and `machine.ADC.__init__(self, pin: Pin)`
/// reading `pin._name` was refused for a program that is correct.
///
/// The resolution is fixed rather than avoided. The receiver's class is taken from the END of
/// the alias chain, which is the caller's value: `ADC(Pin(...))` ends at the Pin, which has
/// `_name`, and `ADC(an_adc)` ends at the ADC, which does not.
///
/// What the old exclusion allowed was not a missing diagnostic but a wrong program: the read
/// was lowered against a slot nothing writes, so a HAL table comparing a pin name became a
/// run-time comparison chain over a byte of BSS, and the `raise CompileError` in its default
/// arm degraded to a warning. A pin with no channel behind it read channel 0.
/// </summary>
public class ReceiverClassThroughHopsTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    private static string Refusal(string src) =>
        Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(src)).Message;

    private const string Classes =
        "from pymcu.types import uint8, inline\n" +
        "from pymcu.chips.atmega328p import GPIOR0\n\n" +
        "class Pin:\n" +
        "    @inline\n" +
        "    def __init__(self, n: uint8):\n" +
        "        self._name = n\n" +
        "class Adc:\n" +
        "    @inline\n" +
        "    def __init__(self, pin: Pin):\n" +
        "        self._ch = pin._name\n" +
        "    @inline\n" +
        "    def read(self) -> uint8:\n" +
        "        return self._ch\n";

    [Fact]
    public void AConstructorReadingItsParametersOwnFieldStillCompiles()
    {
        // The case the exclusion was added for. `pin` IS a Pin and DOES have `_name`; it used
        // to be refused because the lookup answered with the class under construction.
        var ir = Gen(Classes +
            "a = Adc(Pin(3))\n" +
            "def main():\n" +
            "    GPIOR0.value = a.read()\n");

        // And it folds: the channel is the constant the Pin was built with.
        Assert.Contains(ir.Functions.SelectMany(f => f.Body).OfType<Copy>(),
            c => c.Src is Constant k && k.Value == 3);
    }

    [Fact]
    public void AConstructorReadingAFieldTheArgumentsClassDoesNotHaveIsRefused()
    {
        // One hop further: the argument is an Adc, which has `_ch` and not `_name`. This used
        // to be accepted and lowered against a slot nothing writes.
        string msg = Refusal(Classes +
            "inner = Adc(Pin(3))\n" +
            "outer = Adc(inner)\n" +
            "def main():\n" +
            "    GPIOR0.value = outer.read()\n");

        Assert.Contains("has no attribute '_name'", msg);
        Assert.Contains("Adc", msg);
        // It names what the class DOES have, so the reader can see which one arrived.
        Assert.Contains("_ch", msg);
    }

    [Fact]
    public void TheRefusalIsAnErrorAndNotAWarning()
    {
        // The point of the issue: a read the compiler cannot resolve used to become a run-time
        // comparison chain, and a `raise CompileError` guarding it became a warning. A program
        // that cannot be lowered correctly does not build.
        var saved = Console.Error;
        var buf = new StringWriter();
        Console.SetError(buf);
        try
        {
            Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(Classes +
                "inner = Adc(Pin(3))\n" +
                "outer = Adc(inner)\n" +
                "def main():\n" +
                "    GPIOR0.value = outer.read()\n"));
        }
        finally { Console.SetError(saved); }
    }

    [Fact]
    public void AnIdentityOverloadIsWhatMakesTheSecondHopMeanSomething()
    {
        // The other half, and what the compat layer now carries: a class that accepts one of
        // its own takes the field through, so the value keeps its compile-time identity across
        // the hop and the table folds exactly as it does without the wrapper.
        var ir = Gen(
            "from pymcu.types import uint8, inline\n" +
            "from pymcu.chips.atmega328p import GPIOR0\n\n" +
            "class Pin:\n" +
            "    @inline\n" +
            "    def __init__(self, n: uint8):\n" +
            "        self._name = n\n" +
            "class Adc:\n" +
            "    @inline\n" +
            "    def __init__(self, pin: Pin):\n" +
            "        self._ch = pin._name\n" +
            "    @inline\n" +
            "    def __init__(self, other: Adc):\n" +
            "        self._ch = other._ch\n" +
            "    @inline\n" +
            "    def read(self) -> uint8:\n" +
            "        return self._ch\n" +
            "inner = Adc(Pin(3))\n" +
            "outer = Adc(inner)\n" +
            "def main():\n" +
            "    GPIOR0.value = outer.read()\n");

        Assert.Contains(ir.Functions.SelectMany(f => f.Body).OfType<Copy>(),
            c => c.Src is Constant k && k.Value == 3);
    }
}
