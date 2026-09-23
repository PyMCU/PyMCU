using FluentAssertions;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// A call inside nested `try` bodies was guarded by EVERY enclosing try: the inner
/// pass inserted its BranchOnError first, then the outer pass prepended its own at
/// callIndex+1, so the OUTER dispatch sat closer to the call and won the BRTS. The
/// inner `except` never saw the error -- adafruit_bus_device's __probe_for_device
/// (`try: writeto except OSError: try: readfrom ...`) lost its NACK to the caller's
/// `except ValueError`, which mismatched and halted unhandled.
///
/// Only the innermost enclosing try may guard a call; each dispatch's no-match tail
/// already re-signals (SignalError code 0, R22 preserved) to the next enclosing
/// dispatch, so semantics chain outward one level at a time.
/// </summary>
public class NestedTryGuardOrderTests
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
        "        try:\n" +
        "            boom()\n" +
        "        except OSError:\n" +
        "            pass\n" +
        "    except OSError:\n" +
        "        pass\n";

    [Fact]
    public void ACallInNestedTries_IsGuardedByTheInnermostTryOnly()
    {
        var main = Gen(Program).Functions.Single(f => f.Name == "main");

        int callIndex = main.Body.FindIndex(i => i is Call { FunctionName: "boom" });
        callIndex.Should().BeGreaterThanOrEqualTo(0);

        var followers = main.Body.Skip(callIndex + 1)
            .TakeWhile(i => i is BranchOnError or DebugLine)
            .ToList();
        var guards = followers.OfType<BranchOnError>().ToList();
        guards.Should().ContainSingle("only the innermost enclosing try guards the call");

        string innerDispatch = guards[0].ErrorLabel;
        main.Body.Should().Contain(i => i is Label && ((Label)i).Name == innerDispatch,
            "the inner dispatch label exists");

        var resignal = main.Body.OfType<SignalError>()
            .Where(se => se.Code is Constant c && c.Value == 0 && se.CatchLabel != null)
            .Select(se => se.CatchLabel!)
            .ToList();
        resignal.Should().NotBeEmpty("an unmatched inner error re-signals outward");
        foreach (var target in resignal)
            target.Should().NotBe(innerDispatch,
                "the no-match re-signal targets the enclosing dispatch, not itself");
        foreach (var target in resignal)
            main.Body.Should().Contain(i => i is Label && ((Label)i).Name == target,
                "every re-signal target label is emitted");
    }

    [Fact]
    public void ACallInTheOuterBodyButOutsideTheInnerTry_KeepsItsOwnGuard()
    {
        var main = Gen(
            "def boom():\n" +
            "    raise OSError(\"x\")\n\n" +
            "def main():\n" +
            "    try:\n" +
            "        try:\n" +
            "            boom()\n" +
            "        except OSError:\n" +
            "            pass\n" +
            "        boom()\n" +
            "    except OSError:\n" +
            "        pass\n").Functions.Single(f => f.Name == "main");

        var callIndices = main.Body
            .Select((ins, i) => (ins, i))
            .Where(t => t.ins is Call { FunctionName: "boom" })
            .Select(t => t.i)
            .ToList();
        callIndices.Should().HaveCount(2);

        foreach (int callIndex in callIndices)
        {
            var guards = main.Body.Skip(callIndex + 1)
                .TakeWhile(i => i is BranchOnError or DebugLine)
                .OfType<BranchOnError>()
                .ToList();
            guards.Should().ContainSingle(
                $"call at index {callIndex} is guarded by exactly one dispatch");
        }
    }
}
