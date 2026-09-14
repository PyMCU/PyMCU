using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU#330. `if port_of(a) != port_of(b): raise CompileError(...)` is the natural way to say
/// "these two pins have to agree", and it was the one shape that could not say it. Both calls
/// are @inline, both parameters are `const`, and each call folds to a literal on its own -- but
/// the literal is recorded under the RESULT TEMPORARY'S NAME, and the condition path compared
/// the raw `Val`s. A `Temporary` whose name is in `constantVariables` was indistinguishable, at
/// that line, from a genuine run-time temporary.
///
/// So the branch was classified as run-time, the guard inside it became "could not be verified",
/// and the advice told the reader to declare as `const` a parameter their program had already
/// declared as `const`. A HAL author could not tell that false warning from a real one on the
/// same line.
/// </summary>
[Collection(ConsoleCaptureCollection.Name)]
public class InlineCallGuardFoldTests
{
    private const string Ports =
        "from pymcu.exceptions import CompileError\n" +
        "from pymcu.types import uint8, inline, const\n\n" +
        "@inline\n" +
        "def port_of(pin: const) -> uint8:\n" +
        "    match pin:\n" +
        "        case \"PB0\":\n" +
        "            return 0\n" +
        "        case _:\n" +
        "            return 2\n\n";

    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    /// The warning goes to stderr, so the test reads it there. It is a warning and not an
    /// exception precisely because the compiler could not decide the guard; its absence is
    /// the whole measurement.
    private static (string Err, PyMCU.Common.CompilerError? Ex) Build(string src)
    {
        var saved = Console.Error;
        var buf = new StringWriter();
        Console.SetError(buf);
        try
        {
            try { Gen(src); return (buf.ToString(), null); }
            catch (PyMCU.Common.CompilerError e) { return (buf.ToString(), e); }
        }
        finally { Console.SetError(saved); }
    }

    [Fact]
    public void TwoInlineCallsThatBothFold_DecideTheGuardInsteadOfWarning()
    {
        // Both are on port D, so the condition is false and the guard can never be reached.
        var (err, ex) = Build(Ports +
            "@inline\n" +
            "def check(pin_a: const, pin_b: const):\n" +
            "    if port_of(pin_a) != port_of(pin_b):\n" +
            "        raise CompileError(\"the two pins have to be on the same port.\")\n\n" +
            "def main():\n" +
            "    check(\"PD2\", \"PD3\")\n");

        Assert.Null(ex);
        Assert.DoesNotContain("could not be verified", err);
    }

    [Fact]
    public void TwoInlineCallsThatFoldToDifferentValues_RaiseTheGuardRatherThanWarn()
    {
        // PB0 folds to 0 and PD3 to 2, so the condition is TRUE: the raise is statically
        // reachable and the refusal the HAL author wrote is the answer.
        var (err, ex) = Build(Ports +
            "@inline\n" +
            "def check(pin_a: const, pin_b: const):\n" +
            "    if port_of(pin_a) != port_of(pin_b):\n" +
            "        raise CompileError(\"the two pins have to be on the same port.\")\n\n" +
            "def main():\n" +
            "    check(\"PB0\", \"PD3\")\n");

        Assert.NotNull(ex);
        Assert.Contains("the two pins have to be on the same port.", ex!.Message);
        Assert.DoesNotContain("could not be verified", err);
    }

    [Fact]
    public void AnInlineCallComparedAgainstALiteral_FoldsToo()
    {
        var (err, ex) = Build(Ports +
            "@inline\n" +
            "def only_b(pin: const):\n" +
            "    if port_of(pin) != 0:\n" +
            "        raise CompileError(\"this one has to be on port B.\")\n\n" +
            "def main():\n" +
            "    only_b(\"PB0\")\n");

        Assert.Null(ex);
        Assert.DoesNotContain("could not be verified", err);
    }

    [Fact]
    public void AGuardOnARunTimeValueStillWarns_AndTheAdviceNamesWhatIsRunTime()
    {
        // The parameter is a run-time uint8, so there is nothing to fold and the warning is
        // the true answer. What it must NOT do is tell the reader to write `const` on a
        // parameter that is already `const`.
        var (err, ex) = Build(
            "from pymcu.exceptions import CompileError\n" +
            "from pymcu.types import uint8, inline, const\n" +
            "from pymcu.chips.atmega328p import GPIOR0\n\n" +
            "@inline\n" +
            "def check(mode: uint8):\n" +
            "    if mode != 0:\n" +
            "        raise CompileError(\"mode has to be zero.\")\n\n" +
            "def main():\n" +
            "    check(GPIOR0.value)\n");

        Assert.Null(ex);
        Assert.Contains("could not be verified", err);
        // The value the compiler could not decide, by the name the program gave it -- and NOT
        // the old sentence, which told a reader whose parameters were already `const` to
        // declare them `const`.
        Assert.Contains("`mode` is not known at compile time here", err);
        Assert.DoesNotContain("Ensure the guarding parameter is declared as const", err);
    }
}
