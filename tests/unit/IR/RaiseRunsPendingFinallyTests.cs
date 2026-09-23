using FluentAssertions;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// A `raise` inside an `except` handler of a try-with-finally skipped the finally:
/// the SignalError jumped straight to the enclosing dispatch, so
/// `try: ... except OSError: raise ValueError ... finally: unlock()` in
/// adafruit_bus_device's __probe_for_device propagated without unlocking the bus,
/// and the next `try_lock` spun forever. Python unwinds through the finally first;
/// the raise now emits the pending finallys of the tries it escapes (innermost out)
/// before signalling, floored at the target try's own finally -- that one still runs
/// at the dispatch, not at the raise site.
/// </summary>
public class RaiseRunsPendingFinallyTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    private const string Program =
        "def boom():\n" +
        "    raise OSError(\"x\")\n\n" +
        "def main():\n" +
        "    try:\n" +
        "        boom()\n" +
        "    except OSError:\n" +
        "        raise ValueError(\"y\")\n" +
        "    finally:\n" +
        "        marker = 7\n";

    private static int IndexAfterDebugLines(List<Instruction> body, int from)
    {
        int j = from;
        while (j < body.Count && body[j] is DebugLine) j++;
        return j;
    }

    [Fact]
    public void ARaiseInsideAnExceptHandler_RunsTheFinallyBeforeSignalling()
    {
        var main = Gen(Program).Functions.Single(f => f.Name == "main");

        int sigIdx = main.Body.FindIndex(i =>
            i is SignalError { Code: Constant { Value: > 0 } });
        sigIdx.Should().BeGreaterThan(0, "the handler's raise ValueError signals nonzero");

        // The instruction immediately before the signal (modulo debug markers) must
        // be the finally body's store — `marker = 7` ran while unwinding.
        int j = sigIdx - 1;
        while (j >= 0 && main.Body[j] is DebugLine) j--;
        var copy = main.Body[j].Should().BeOfType<Copy>(
            "the pending finally's statements emit inline at the raise site").Subject;
        copy.Dst.Should().BeOfType<Variable>()
            .Which.Name.Should().EndWith("marker");
        copy.Src.Should().BeOfType<Constant>()
            .Which.Value.Should().Be(7);
    }

    [Fact]
    public void ARaiseInTheTryBody_DoesNotRunTheOwnFinallyInline()
    {
        var main = Gen(
            "def boom():\n" +
            "    raise OSError(\"x\")\n\n" +
            "def main():\n" +
            "    try:\n" +
            "        raise ValueError(\"y\")\n" +
            "    except ValueError:\n" +
            "        pass\n" +
            "    finally:\n" +
            "        marker = 7\n").Functions.Single(f => f.Name == "main");

        int sigIdx = main.Body.FindIndex(i =>
            i is SignalError { Code: Constant { Value: > 0 } });
        sigIdx.Should().BeGreaterThanOrEqualTo(0);

        // The raise lands on this try's OWN dispatch, whose no-match/handler tails
        // run the finally — emitting it at the raise site too would run it twice.
        int j = sigIdx - 1;
        while (j >= 0 && main.Body[j] is DebugLine) j--;
        (j < 0 || main.Body[j] is not Copy).Should().BeTrue(
            "the try body's raise is caught by the same try, so its finally runs at the dispatch");
    }
}
