using FluentAssertions;
using PyMCU.Common;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// `return (a, b) if cond else (a, b, c)` -- a tuple that lives in a conditional
/// expression's arm. The caller's result slots have a fixed arity before the callee's
/// body runs, so this is only compilable when the condition folds at compile time:
/// VisitReturn then lowers the taken arm as an ordinary tuple return.
///
/// Reduced from adafruit_neopixel's `wheel()`:
/// `return (r, g, b) if ORDER in {RGB, GRB} else (r, g, b, 0)` -- ORDER is a module-level
/// string constant, so the `in` test is a compile-time answer, and the caller
/// `pixels[i] = wheel(pixel_index & 255)` hands the delivered slots to `__setitem__`
/// as the sequence `val` unpacks.
/// </summary>
public class ConditionalTupleReturnTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });

    private static CompilerError Fails(string src) =>
        Assert.ThrowsAny<CompilerError>(() => Gen(src));

    private const string Preamble =
        "from pymcu.chips.atmega328p import GPIOR0, GPIOR1\n" +
        "from pymcu.types import uint8\n\n";

    // The wheel() shape: a compile-time string membership picks the arm.
    private const string Wheel =
        "RGB = \"RGB\"\n" +
        "GRB = \"GRB\"\n" +
        "ORDER = GRB\n\n" +
        "@inline\n" +
        "def wheel(pos: uint8):\n" +
        "    r = pos\n" +
        "    return (r, 2, 3) if ORDER in {RGB, GRB} else (r, 2, 3, 0)\n";

    /// <summary>The slot names a tuple-returning call writes, in order.</summary>
    private static List<string> SlotsWritten(ProgramIR ir) =>
        ir.Functions.SelectMany(f => f.Body).OfType<Copy>()
            .Select(c => c.Dst).OfType<Variable>()
            .Select(v => v.Name).Where(n => n.Contains("iret_")).ToList();

    [Fact]
    public void TheTakenArmDeterminesTheArity()
    {
        var ir = Gen(Preamble + Wheel +
            "def main():\n    a, b, c = wheel(5)\n");

        // ORDER is "GRB", so the 3-element arm is the return: exactly three slots.
        var slots = SlotsWritten(ir).Where(n => n.EndsWith("_0") || n.EndsWith("_1")
                                                || n.EndsWith("_2") || n.EndsWith("_3")).ToList();
        slots.Should().HaveCount(3,
            because: "ORDER in {RGB, GRB} folds true, so wheel returns (r, 2, 3)");
    }

    [Fact]
    public void TheOtherArmIsSelectedWhenTheMembershipFoldsFalse()
    {
        var ir = Gen(Preamble +
            "RGB = \"RGB\"\n" +
            "GRB = \"GRB\"\n" +
            "ORDER = \"RGBW\"\n\n" +
            "@inline\n" +
            "def wheel(pos: uint8):\n" +
            "    r = pos\n" +
            "    return (r, 2, 3) if ORDER in {RGB, GRB} else (r, 2, 3, 0)\n" +
            "def main():\n    a, b, c, d = wheel(5)\n");

        var slots = SlotsWritten(ir).Where(n => n.EndsWith("_0") || n.EndsWith("_1")
                                                || n.EndsWith("_2") || n.EndsWith("_3")).ToList();
        slots.Should().HaveCount(4,
            because: "ORDER is \"RGBW\", so the else arm's (r, 2, 3, 0) is the return");
    }

    [Fact]
    public void ACallResultBindsAsTheSequenceRhsOfASubscriptStore()
    {
        // pixels[i] = wheel(...): the delivered slots are the `val` __setitem__ unpacks.
        var ir = Gen(Preamble + Wheel +
            "class Strip:\n" +
            "    @inline\n" +
            "    def __init__(self):\n" +
            "        pass\n" +
            "    @inline\n" +
            "    def __setitem__(self, index: uint8, val):\n" +
            "        r, g, b = val\n" +
            "        PORTB: ptr[uint8] = ptr(0x25)\n" +
            "        PORTB.value = r\n\n" +
            "s = Strip()\n" +
            "def main():\n    s[0] = wheel(7)\n");

        ir.Functions.Should().Contain(f => f.Name == "main",
            because: "the tuple a call returns is the sequence a subscript-store binds");
    }

    [Fact]
    public void ARuntimeConditionIsRefused()
    {
        // The slot arity is decided before the body runs; a condition the compiler
        // cannot fold leaves no answer for how many slots to write.
        var ex = Fails(Preamble +
            "@inline\n" +
            "def f(v: uint8):\n" +
            "    return (v, 1) if v > 3 else (v, 1, 2)\n" +
            "def main():\n    a, b = f(GPIOR0.value)\n");

        ex.Message.Should().Contain("must decide at compile time",
            because: "a run-time condition leaves the return arity undecidable");
    }

    // ── the str-lhs `in` fold the condition relies on ─────────────────────────────

    [Fact]
    public void MembershipOfAStringConstantAgainstStringConstantsFolds()
    {
        // `ORDER in {RGB, GRB}`: the lhs is a name bound to a compile-time string and
        // the elements are string constants, so the answer is a fold, not a compare
        // chain.
        var body = Gen(Preamble +
            "RGB = \"RGB\"\n" +
            "GRB = \"GRB\"\n" +
            "ORDER = GRB\n" +
            "def main():\n" +
            "    if ORDER in {RGB, GRB}:\n" +
            "        GPIOR1.value = 9\n" +
            "    else:\n" +
            "        GPIOR1.value = 3\n").Functions.Single(f => f.Name == "main").Body;

        body.Any(i => i is Copy { Src: Constant { Value: 9 } }).Should().BeTrue(
            because: "\"GRB\" is an element, so the true arm is the whole program");
        body.Any(i => i is Copy { Src: Constant { Value: 3 } }).Should().BeFalse(
            because: "the false arm is dead once the fold answers");
        body.Any(i => i is JumpIfZero or JumpIfNotZero).Should().BeFalse(
            because: "a compile-time membership must not leave a runtime compare");
    }

    /// <summary>
    /// An unannotated tuple return's element slots take each element's own width: a
    /// `uint16` member must not truncate to the uint8 default the slot used to mint
    /// with. The byte store keeps the low byte -- but through a real UINT16 slot,
    /// not a silent narrowing at the marshalling boundary.
    /// </summary>
    [Fact]
    public void AWideElementKeepsItsWidthInTheResultSlot()
    {
        var body = Gen(
            "from pymcu.types import uint8, uint16, inline, ptr\n" +
            "G: ptr[uint16] = ptr(0x40)\n" +
            "B: ptr[uint8] = ptr(0x3E)\n" +
            "@inline\n" +
            "def pair(w: uint16):\n" +
            "    return (w, 7)\n" +
            "def main():\n" +
            "    hi, lo = pair(G.value)\n" +
            "    B.value = hi\n").Functions.Single(f => f.Name == "main").Body;

        var slotCopy = body.OfType<Copy>().SingleOrDefault(c =>
            c.Src is Variable { Name: var sn } && sn.EndsWith("pair.w")
            && c.Dst is Variable { Name: var dn } && dn.Contains("iret_"));
        slotCopy.Should().NotBeNull(because: "the first tuple element marshals into an iret slot");
        ((Variable)slotCopy!.Dst).Type.Should().Be(DataType.UINT16,
            because: "the slot takes the element's own width, not the uint8 default");
    }
}
