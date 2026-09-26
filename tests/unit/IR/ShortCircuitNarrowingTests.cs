using FluentAssertions;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// `and` and `or` short-circuit, so their right operand is evaluated only on the path the
/// left one decided. That path is a proof: `last is None or pos != last` reaches the
/// comparison only where `last is not None`, and `last is not None and pos != last` only
/// where the same holds. The jump chain an `if` condition becomes lowered the right
/// operand with the pre-condition state, so the read was refused with the narrowing
/// already written on the line (PyMCU#513).
///
/// Both spellings of the same Python are pinned side by side. A name whose Optional
/// character is INFERRED from a bare `= None` is the one real code writes and the only one
/// that was refused, because the declared spelling takes a different route through the
/// generator: a green annotated test could sit next to a broken unannotated one and hide
/// it. What the two spellings still do differently is pinned below, not assumed away.
/// </summary>
public class ShortCircuitNarrowingTests
{
    private static ProgramIR Gen(string src)
    {
        var ir = new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });
        return Optimizer.Optimize(ir);
    }

    // The demandant's shape (pymcu-circuitpython's 14_rotary_encoder): `last` bound to None
    // before the loop, rebound to a value inside it, and guarded by the idiom that puts the
    // narrowing and the payload read on the same line.
    private static string Loop(string decl, string cond) =>
        "sink: int = 0\n" +
        decl + "\n" +
        "i: int = 0\n" +
        "while i < 10:\n" +
        "    pos: int = i * 3\n" +
        "    if " + cond + ":\n" +
        "        sink = sink + 1\n" +
        "    last = pos\n" +
        "    i = i + 1\n";

    private const string Inferred = "last = None";
    private const string Declared = "last: int | None = None";
    private const string OrCond   = "last is None or pos != last";
    private const string AndCond  = "last is not None and pos != last";

    private static List<Instruction> Body(string src) =>
        Gen(src).Functions.SelectMany(f => f.Body).ToList();

    private static string? NameOf(Val v) => v switch
    {
        Variable var => var.Name,
        Temporary t => t.Name,
        _ => null,
    };

    private static IEnumerable<string> SourceNames(Instruction ins)
    {
        foreach (var v in ins switch
        {
            Copy c => new[] { c.Src },
            Binary b => new[] { b.Src1, b.Src2 },
            JumpIfEqual j => new[] { j.Src1, j.Src2 },
            JumpIfNotEqual j => new[] { j.Src1, j.Src2 },
            JumpIfLessThan j => new[] { j.Src1, j.Src2 },
            JumpIfLessOrEqual j => new[] { j.Src1, j.Src2 },
            JumpIfGreaterThan j => new[] { j.Src1, j.Src2 },
            JumpIfGreaterOrEqual j => new[] { j.Src1, j.Src2 },
            _ => Array.Empty<Val>(),
        })
            if (NameOf(v) is { } n) yield return n;
    }

    /// The tag byte of `last` read as the thing a jump is decided on.
    private static int TagTests(List<Instruction> body) =>
        body.Count(i => i is JumpIfEqual or JumpIfNotEqual
                        && SourceNames(i).Any(n => n.EndsWith("last$tag")));

    /// The payload of `last` read as a value -- what the right operand of the
    /// short-circuit asks for and what the refusal used to stop.
    private static int PayloadReads(List<Instruction> body) =>
        body.Count(i => SourceNames(i).Any(n => n == "last" || n.EndsWith(".last")));

    [Fact]
    public void OrNarrowsItsRightOperand_InferredOptional()
    {
        var body = Body(Loop(Inferred, OrCond));

        TagTests(body).Should().BeGreaterThan(0,
            because: "`last is None` inside the loop is decided by the tag byte, not folded");
        PayloadReads(body).Should().BeGreaterThan(0,
            because: "`or` reaches `pos != last` only where last is not None, so the "
                     + "payload read on that operand is allowed (PyMCU#513)");
    }

    [Fact]
    public void AndNarrowsItsRightOperand_InferredOptional()
    {
        var body = Body(Loop(Inferred, AndCond));

        TagTests(body).Should().BeGreaterThan(0,
            because: "`last is not None` inside the loop is decided by the tag byte");
        PayloadReads(body).Should().BeGreaterThan(0,
            because: "`and` reaches `pos != last` only where last is not None");
    }

    [Fact]
    public void OrNarrowsItsRightOperand_DeclaredOptional()
    {
        PayloadReads(Body(Loop(Declared, OrCond))).Should().BeGreaterThan(0,
            because: "the declared spelling means the same Python and reads the payload too");
    }

    [Fact]
    public void AndNarrowsItsRightOperand_DeclaredOptional()
    {
        PayloadReads(Body(Loop(Declared, AndCond))).Should().BeGreaterThan(0,
            because: "the declared spelling means the same Python and reads the payload too");
    }

    [Fact]
    public void TheDeclaredSpellingStillCarriesNoTag()
    {
        // MEASURED, not wanted: `last: int | None = None` at module level keeps no tag byte
        // at all, so `last is None` folds away and the first iteration takes the wrong arm.
        // That is a separate defect from #513 and it is what made the declared spelling look
        // like the working one. Pinned here so the difference between the two spellings is
        // visible; this turns red when the declared spelling starts carrying its tag, and
        // the right answer then is to assert the tag test instead of its absence.
        TagTests(Body(Loop(Declared, OrCond))).Should().Be(0);
        TagTests(Body(Loop(Inferred, OrCond))).Should().BeGreaterThan(0);
    }

    [Fact]
    public void TheOperandProofDoesNotReachTheArm()
    {
        // `last is None or i > 3` proves nothing about `last` to the arm it guards: the arm
        // runs on both paths and one of them holds None. The narrowing borrowed for the
        // right operand must be handed back, which shows as the arm's own tag dispatch
        // surviving -- one test for the `is None`, one guarding the payload read inside.
        var body = Body(Loop(Inferred, "last is None or i > 3")
                            .Replace("sink = sink + 1", "sink = sink + last"));

        TagTests(body).Should().BeGreaterThan(1,
            because: "the arm keeps its own run-time dispatch; the operand's proof is "
                     + "taken back at the end of the operand");
    }
}
