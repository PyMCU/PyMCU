using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU#225. A failing `assert` was reported only when the condition was the literal `0`.
///
/// VisitAssert evaluated the condition and the catch swallowed everything that was not already
/// an AssertionError. EvaluateConstantExpr folds an integer literal, so `0` arrived; it folds
/// neither a comparison nor a BooleanLiteral, so `assert 1 == 2` and `assert False` both threw,
/// were both swallowed, and lowered to nothing. The only row that agreed with CPython was the
/// one nobody writes.
///
/// The folding is local to `assert`: teaching EvaluateConstantExpr to fold comparisons would
/// change every caller that asks it for an array size or an address.
///
/// The non-constant case still emits no code and is covered in tests/driver, where a warning on
/// stderr can be read from a subprocess instead of by swapping Console.Error under a shared
/// test host.
/// </summary>
public class AssertFoldingTests
{
    // Since PyMCU#331 a read of a local whose value the compiler tracks folds to that value, so
    // a literal in a local no longer describes anything decided at run time. G is a register the
    // compiler cannot read, which makes G.value the shortest run-time value a body can ask for.
    private const string Preamble =
        "from pymcu.types import uint8, ptr\n" +
        "G: ptr[uint8] = ptr(0x3E)\n";

    private static ProgramIR Gen(string body) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(Preamble + "def main() -> None:\n" + body + "\n").Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    private static string Refusal(string body)
        => Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(body)).Message;

    // ─── every spelling of a false assertion ──────────────────────────────

    [Theory]
    [InlineData("    assert 0")]            // the one row that already worked
    [InlineData("    assert False")]
    [InlineData("    assert 1 == 2")]
    [InlineData("    assert 2 < 1")]
    [InlineData("    assert 1 != 1")]
    [InlineData("    assert not True")]
    [InlineData("    assert 0 and 1")]      // short-circuit: false decides it
    [InlineData("    assert False or 0")]
    public void AStaticallyFalseAssert_IsRefused(string body)
        => Assert.Contains("AssertionError", Refusal(body));

    [Fact]
    public void TheMessageIsCarried()
        => Assert.Contains("AssertionError: nope", Refusal("    assert 1 == 2, \"nope\""));

    // `assert 0` was refused inside a run-time branch and inside an else, so refusing wherever
    // it stands is the policy that already shipped rather than a new one. Pinned because it is
    // the trade: `assert False` cannot mark an unreachable branch, and could not when spelled
    // `assert 0` either.
    //
    // The branch has to be a genuine run-time one. These rows were written as `x: uint8 = 1`,
    // which since PyMCU#331 is a compile-time value: the compiler decides `x == 3` is false,
    // drops the branch, and the assert inside it is never visited, so the row stopped saying
    // anything about where an assert stands. Reading x from a register restores the run-time
    // branch the rows were about.
    [Theory]
    [InlineData("    x: uint8 = G.value\n    if x == 3:\n        assert False")]
    [InlineData("    x: uint8 = G.value\n    if x == 3:\n        x = 2\n    else:\n        assert 1 == 2")]
    public void AFalseAssertIsRefusedWhereverItStands(string body)
        => Assert.Contains("AssertionError", Refusal(body));

    // ─── true, and not-known: neither may refuse ──────────────────────────

    [Theory]
    [InlineData("    assert 1")]
    [InlineData("    assert True")]
    [InlineData("    assert 2 == 2")]
    [InlineData("    assert 1 < 2")]
    [InlineData("    assert not False")]
    [InlineData("    assert 1 and 2")]
    [InlineData("    assert 0 or 1")]
    public void AStaticallyTrueAssert_CompilesToNothing(string body) => Assert.NotNull(Gen(body));

    // The condition the compiler cannot resolve must not become a refusal: that would turn
    // every ordinary invariant into a compile error.
    [Theory]
    [InlineData("    x: uint8 = 1\n    assert x == 2")]
    [InlineData("    x: uint8 = 1\n    assert x")]
    [InlineData("    x: uint8 = 1\n    assert x < 10 and x > 0")]
    public void AConditionThatDoesNotFold_IsNotRefused(string body) => Assert.NotNull(Gen(body));
}
