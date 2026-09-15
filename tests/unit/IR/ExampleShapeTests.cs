using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU#374 and #375: the two constructs an Adafruit example opens with.
///
/// `hcsr04_simpletest.py` builds its sensor with a keyword-only default it does not pass, and
/// prints a ONE-ELEMENT TUPLE, because the Mu plotter reads a printed tuple. Neither has to
/// exist at run time, and both were refused.
/// </summary>
public class ExampleShapeTests
{
    private const string Hdr = "from pymcu.chips.atmega328p import GPIOR0\n\n";

    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    private const string Sonar =
        "class Sonar:\n" +
        "    def __init__(self, trig: int, echo: int, *, timeout: float = 0.1) -> None:\n" +
        "        self._timeout = timeout\n" +
        "        self._trig = trig\n" +
        "    def hold(self) -> float:\n" +
        "        return self._timeout\n";

    [Fact]
    public void AnOmittedKeywordOnlyFloatDefaultIsReceived()
    {
        // It was reported as a name the function never received, inside the function that
        // declares it, because a name bound ONLY as a float constant was not "known" to the
        // definedness check and no read answered with the float.
        Assert.NotNull(Gen(Hdr + Sonar + "s = Sonar(5, 2)\n\ndef main() -> None:\n    h: float = s.hold()\n    GPIOR0.value = 1\n"));
    }

    [Fact]
    public void TheDefaultsValueReachesTheField()
    {
        // The half that matters: building is not enough, the field has to hold 0.1. It held
        // 0.0 while the read resolved to a run-time slot nothing writes.
        var ir = Gen(Hdr + Sonar + "s = Sonar(5, 2)\n\ndef main() -> None:\n    h: float = s.hold()\n    GPIOR0.value = 1\n");
        Assert.Contains(ir.Functions.SelectMany(f => f.Body).OfType<Copy>(),
            c => c.Src is FloatConstant f && System.Math.Abs(f.Value - 0.1) < 1e-9);
    }

    [Fact]
    public void AnOmittedIntDefaultIsUnchanged()
    {
        Assert.NotNull(Gen(
            "class Box:\n" +
            "    def __init__(self, a: int, *, n: int = 7) -> None:\n" +
            "        self._n = n\n" +
            "def main() -> None:\n" +
            "    b = Box(1)\n" +
            "    y = b._n\n"));
    }

    [Fact]
    public void AMissingRequiredArgumentIsStillRefused()
    {
        // The refusal this must not weaken: a parameter with NO default and no argument.
        var ex = Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(
            "class Box:\n" +
            "    def __init__(self, a: int, b: int) -> None:\n" +
            "        self._a = a\n" +
            "def main() -> None:\n" +
            "    x = Box(1)\n" +
            "    y = x._a\n"));
        Assert.Contains("missing required argument", ex.Message);
    }

    // A PRINTED tuple needs the console HAL, which this single-source harness cannot resolve,
    // so its two facts live in the AVR fixture where the measurement is the text on the wire
    // rather than a build that succeeds: `(12.5,)` with its trailing comma, and `(1, 2)` with
    // comma and space, both exactly as CPython writes them.

    // A tuple used as a VALUE keeps its refusal, and there is no unit test for it here
    // because the shapes that reach it need a consumer this single-source harness cannot
    // build: a tuple of constants bound to a name is a compile-time sequence (#299) and an
    // unused one is dead code. The guarantee is structural instead -- the new path lives
    // inside EmitPrintArg and returns before any other consumer sees the node -- and the
    // 322-fixture corpus is byte-identical, which is what says nothing else moved.
}
