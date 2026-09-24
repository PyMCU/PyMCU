using FluentAssertions;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// A name bound to <c>None</c> before a loop and rebound to a value inside it must answer
/// <c>x is None</c> from its tag byte, not from the pre-loop compile-time record: the body
/// is lowered once but runs many times, so the entry state's "x is None" is only true on
/// iteration one. Folding it made <c>received = []</c> inside <c>if received is None:</c>
/// unconditional -- adafruit_irremote's read loop kept a one-element list forever -- and a
/// second gap kept the inferred member list at <c>uint8</c>, so <c>return received</c>
/// under <c>-&gt; Optional[list]</c> refused the remap.
/// </summary>
public class LoopNoneRebindTests
{
    private static ProgramIR Gen(string src)
    {
        var ir = new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });
        return Optimizer.Optimize(ir);
    }

    // The demandant's shape: a collector written `received = None` ahead of the loop and
    // `received = []` inside an `is None` arm, then appended to every iteration.
    private const string Collect =
        "def collect():\n" +
        "    received = None\n" +
        "    i = 0\n" +
        "    while i < 5:\n" +
        "        pulse = i\n" +
        "        if received is None:\n" +
        "            received = []\n" +
        "        received.append(pulse)\n" +
        "        i = i + 1\n" +
        "    return received\n";

    [Fact]
    public void DefiniteWriteRenamesTheInferredMemberList()
    {
        // `received = []` after `received = None`: the inferred ["uint8", "None"] member
        // list must rename member 0 to "list" or the tagged return remaps uint8 into
        // Union[list, None] and refuses.
        var ir = Gen(
            "def collect() -> Optional[list]:\n" +
            "    received = None\n" +
            "    received = []\n" +
            "    received.append(5)\n" +
            "    return received\n" +
            "\ndef main():\n    collect()\n");

        ir.Functions.SelectMany(f => f.Body).OfType<Return>()
            .Should().NotBeEmpty(because: "the optional return must compile");
    }

    [Fact]
    public void IsNoneInsideALoopReadsTheTag_NotTheEntryState()
    {
        var ir = Gen(Collect + "\ndef main():\n    collect()\n");

        var body = ir.Functions.Single(f => f.Name == "collect").Body;
        // A folded check emits no comparison at all. The honest lowering compares the
        // tag byte against the None member index: one JumpIfNotEqual on the `is None`.
        body.OfType<JumpIfNotEqual>()
            .Where(j => j.Src1 is Variable v && v.Name.EndsWith("received$tag"))
            .Should().ContainSingle(
                because: "`received is None` inside a loop must read the run-time tag");
    }
}
