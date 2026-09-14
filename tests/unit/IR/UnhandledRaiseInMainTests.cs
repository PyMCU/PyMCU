using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU#339. An unhandled `raise` in the ENTRY function lowered to the propagate-to-caller
/// form, `SET; RET` -- and main has no caller. The stack pointer is at the top of SRAM, so the
/// RET pops a return address that was never pushed and execution goes wherever those bytes
/// point; avr8sharp reports a stack underflow at that instruction and nothing reaches the UART.
///
/// The documented behaviour is `__pymcu_unhandled_exn`: `E:&lt;TypeName&gt;` on UART0, then a halt.
/// It was emitted only for an exception RETURNING into main from a callee.
/// </summary>
public class UnhandledRaiseInMainTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    private static Function Main(ProgramIR ir) => ir.Functions.Last(f => f.Name == "main");

    private static bool Halts(ProgramIR ir) =>
        Main(ir).Body.OfType<Call>().Any(c => c.FunctionName == "__pymcu_unhandled_exn");

    /// <summary>A SignalError that returns to a caller: the form main must never take.</summary>
    private static bool ReturnsToACaller(ProgramIR ir) =>
        Main(ir).Body.OfType<SignalError>().Any(s => s.CatchLabel == null);

    private const string Prelude =
        "from pymcu.chips.atmega328p import GPIOR0\n" +
        "from pymcu.types import uint8, inline\n" +
        "\n";

    [Fact]
    public void ARaiseInMainsOwnBody_Halts()
    {
        var ir = Gen(Prelude +
            "def main():\n" +
            "    code: uint8 = GPIOR0.value\n" +
            "    if code == 90:\n" +
            "        raise ValueError(\"no glyph\")\n" +
            "    while True:\n" +
            "        pass\n");

        Assert.True(Halts(ir), "the raise must reach __pymcu_unhandled_exn");
        Assert.False(ReturnsToACaller(ir), "main has no caller to return the error to");
    }

    [Fact]
    public void ARaiseReachedThroughAnInlineExpansionInMain_HaltsToo()
    {
        // A driver's `raise` in a method called from the top level, which is where the
        // seven-segment library raises.
        var ir = Gen(Prelude +
            "@inline\n" +
            "def glyph(c: uint8):\n" +
            "    if c == 90:\n" +
            "        raise ValueError(\"no glyph\")\n" +
            "    GPIOR0.value = c\n" +
            "\n" +
            "def main():\n" +
            "    glyph(GPIOR0.value)\n" +
            "    while True:\n" +
            "        pass\n");

        Assert.True(Halts(ir));
        Assert.False(ReturnsToACaller(ir));
    }

    [Fact]
    public void AModuleLevelRaise_HaltsAsWell()
    {
        // No `def main()`: the compiler synthesizes one from the top-level statements, and it
        // has no caller either.
        var ir = Gen(Prelude +
            "code: uint8 = GPIOR0.value\n" +
            "if code == 90:\n" +
            "    raise ValueError(\"no glyph\")\n" +
            "while True:\n" +
            "    pass\n");

        Assert.True(Halts(ir));
        Assert.False(ReturnsToACaller(ir));
    }

    [Fact]
    public void ARaiseInACallee_StillPropagatesToItsCaller()
    {
        // The control: a function that is not the entry point HAS a caller, and the T-flag
        // model is how the error reaches it.
        var ir = Gen(Prelude +
            "def step(c: uint8) -> uint8:\n" +
            "    if c == 90:\n" +
            "        raise ValueError(\"no glyph\")\n" +
            "    return c\n" +
            "\n" +
            "def main():\n" +
            "    GPIOR0.value = step(GPIOR0.value)\n" +
            "    while True:\n" +
            "        pass\n");

        var step = ir.Functions.Single(f => f.Name == "step");
        Assert.Contains(step.Body.OfType<SignalError>(), s => s.CatchLabel == null);
    }

    [Fact]
    public void ARaiseInsideATryInMain_StillReachesItsHandler()
    {
        // The other control: a local handler takes it, and nothing halts.
        var ir = Gen(Prelude +
            "def main():\n" +
            "    try:\n" +
            "        if GPIOR0.value == 90:\n" +
            "            raise ValueError(\"no glyph\")\n" +
            "    except ValueError:\n" +
            "        GPIOR0.value = 7\n" +
            "    while True:\n" +
            "        pass\n");

        Assert.Contains(Main(ir).Body.OfType<SignalError>(), s => s.CatchLabel != null);
    }
}
