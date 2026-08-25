using Xunit;
using FluentAssertions;
using PyMCU.Common;
using PyMCU.Frontend;

namespace PyMCU.UnitTests;

/// <summary>
/// Every `await asyncio.sleep(...)` in a coroutine shares one `_start` timestamp, because
/// only one of them can be suspended at a time. The earlier shape gave each await site its
/// own `_startN`, so a coroutine's state grew by 4 bytes per site; on a 2 KB part that was
/// most of what a task cost. A literal duration stays inline in the comparison, which keeps
/// the wait state and its full 2^32 us range exactly as they were; only a run-time duration
/// is stored, and storing it moves its multiply off the poll path.
/// </summary>
public class AsyncAwaitStateTests
{
    private static ClassDef Lower(string source)
    {
        var ast = new Parser(new Lexer(source).Tokenize()).ParseProgram();
        AsyncTransform.TransformProgram(ast);
        return ast.GlobalStatements.OfType<ClassDef>().Single();
    }

    private static List<AssignStmt> InitFields(ClassDef cls) =>
        ((Block)((Block)cls.Body).Statements.OfType<FunctionDef>()
            .Single(f => f.Name == "__init__").Body).Statements
        .OfType<AssignStmt>()
        .Where(a => a.Target is MemberAccessExpr)
        .ToList();

    private static List<string> FieldNames(ClassDef cls) =>
        InitFields(cls).Select(a => ((MemberAccessExpr)a.Target).Member).ToList();

    [Fact]
    public void ThreeAwaitSites_ShareOneStartField()
    {
        var fields = FieldNames(Lower("""
            import asyncio

            async def job():
                await asyncio.sleep_ms(10)
                await asyncio.sleep_ms(20)
                await asyncio.sleep_ms(30)
            """));

        // Six bytes: the uint16 _state and one uint32 _start. Three awaits used to cost 14.
        fields.Should().Equal("_state", "_start");
    }

    [Fact]
    public void ALiteralDurationNeedsNoDurationField_AndIsFoldedIntoTheComparison()
    {
        var cls = Lower("""
            import asyncio

            async def job():
                await asyncio.sleep_ms(500)
            """);

        FieldNames(cls).Should().NotContain("_duration");

        // The wait compares the elapsed time against the folded microsecond count, so no
        // multiply reaches the target at all.
        var wait = WaitComparisons(cls).Single();
        wait.Right.Should().BeOfType<IntegerLiteral>().Which.Value.Should().Be(500_000);
    }

    [Fact]
    public void ARunTimeDurationIsStoredOnceInsteadOfMultipliedOnEveryPoll()
    {
        var cls = Lower("""
            import asyncio
            from pymcu.types import uint16

            async def job(ms: uint16):
                await asyncio.sleep_ms(ms)
            """);

        FieldNames(cls).Should().Contain("_duration");

        // The multiply lives at the arm site...
        var arm = AllStatements(cls).OfType<AssignStmt>()
            .Single(a => a.Target is MemberAccessExpr { Member: "_duration" }
                         && a.Value is BinaryExpr);   // the __init__ zero-init is the other one
        arm.Value.Should().BeOfType<BinaryExpr>().Which.Op.Should().Be(BinaryOp.Mul);

        // ...and the wait state just reads the field.
        WaitComparisons(cls).Single().Right
            .Should().BeOfType<MemberAccessExpr>()
            .Which.Member.Should().Be("_duration");
    }

    [Fact]
    public void TheWaitStillSubtractsTheSharedStart()
    {
        // `ticks() - start < duration` in wrapping uint32 arithmetic is what makes a
        // counter roll-over during the wait harmless, and what keeps the full 2^32 us
        // range usable. A deadline comparison would be one field fewer and half the range.
        var diff = WaitComparisons(Lower("""
            import asyncio

            async def job():
                await asyncio.sleep_ms(10)
            """)).Single().Left.Should().BeOfType<BinaryExpr>().Which;

        diff.Op.Should().Be(BinaryOp.Sub);
        diff.Right.Should().BeOfType<MemberAccessExpr>().Which.Member.Should().Be("_start");
    }

    [Fact]
    public void ASleepPastTheCounterRangeIsARefusalThatNamesTheLimit()
    {
        // 4295 s is just over 2^32 us. Left alone the subtraction lands back inside the
        // window and the await returns at once, which only shows up on hardware.
        var tooLong = () => Lower("""
            import asyncio

            async def job():
                await asyncio.sleep(4295)
            """);

        tooLong.Should().Throw<SyntaxError>().WithMessage("*4294 seconds*");
    }

    [Fact]
    public void ASleepOfAnHourIsStillAccepted()
    {
        // 3600 s is 3.6e9 us: past int.MaxValue, inside the uint32 counter. This is the
        // range a deadline-based wait would have refused.
        FieldNames(Lower("""
            import asyncio

            async def job():
                await asyncio.sleep(3600)
            """)).Should().Contain("_start");
    }

    [Fact]
    public void ANegativeSleepIsRefused()
    {
        var negative = () => Lower("""
            import asyncio

            async def job():
                await asyncio.sleep_ms(-5)
            """);

        negative.Should().Throw<SyntaxError>().WithMessage("*cannot be negative*");
    }


    [Fact]
    public void AConstantStepThatIsNotABareLiteralIsAccepted()
    {
        // The step check used to match IntegerLiteral directly, so `1 + 1` was refused by
        // a message saying a positive constant step was required, which is what it is.
        var folded = Lower("""
            import asyncio

            async def job():
                for i in range(0, 4, 1 + 1):
                    await asyncio.sleep_ms(1)
            """);

        FieldNames(folded).Should().Contain("_start");
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("0")]
    [InlineData("-(1 + 1)")]
    public void ANonPositiveStepIsStillRefused(string step)
    {
        var bad = () => Lower($"""
            import asyncio

            async def job():
                for i in range(0, 4, {step}):
                    await asyncio.sleep_ms(1)
            """);

        bad.Should().Throw<SyntaxError>().WithMessage("*positive constant step*");
    }

    [Fact]
    public void AFoldedDurationIsRangeCheckedToo()
    {
        // `2 * 3600` seconds is past the counter, and folding is what lets the guard see it.
        var tooLong = () => Lower("""
            import asyncio

            async def job():
                await asyncio.sleep(2 * 3600)
            """);

        tooLong.Should().Throw<SyntaxError>().WithMessage("*4294 seconds*");
    }

    // The `<something> < <duration>` test each wait state is built around.
    private static List<BinaryExpr> WaitComparisons(ClassDef cls) =>
        AllStatements(cls).OfType<IfStmt>()
            .Select(i => i.Condition)
            .OfType<BinaryExpr>()
            .Where(b => b.Op == BinaryOp.Less && b.Left is BinaryExpr { Op: BinaryOp.Sub })
            .ToList();

    private static IEnumerable<Statement> AllStatements(ClassDef cls)
    {
        IEnumerable<Statement> Walk(Statement s)
        {
            yield return s;
            switch (s)
            {
                case Block b:
                    foreach (var x in b.Statements.SelectMany(Walk)) yield return x;
                    break;
                case FunctionDef f:
                    foreach (var x in Walk(f.Body)) yield return x;
                    break;
                case IfStmt i:
                    foreach (var x in Walk(i.ThenBranch)) yield return x;
                    foreach (var (_, body) in i.ElifBranches)
                        foreach (var x in Walk(body)) yield return x;
                    if (i.ElseBranch != null)
                        foreach (var x in Walk(i.ElseBranch)) yield return x;
                    break;
            }
        }

        return Walk(cls.Body);
    }
}
