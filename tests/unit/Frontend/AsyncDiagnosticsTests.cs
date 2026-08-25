using Xunit;
using FluentAssertions;
using PyMCU.Common;
using PyMCU.Frontend;

namespace PyMCU.UnitTests;

/// <summary>
/// Two properties every diagnostic the async desugar raises has to have, and neither of which
/// it had: a line, and a description of the Python that was written.
///
/// Without a line the message comes out as `file:0:1`, the caret block prints nothing, and a
/// module with more than one coroutine does not say which `await` is meant. And a message
/// naming `VarDecl` or `ExprStmt` names a class in this compiler, not anything the reader
/// wrote: those two are an annotated assignment and an `await` used as a value, which are
/// different mistakes with different fixes.
/// </summary>
public class AsyncDiagnosticsTests
{
    private static SyntaxError Reject(string source)
    {
        var ast = new Parser(new Lexer(source).Tokenize()).ParseProgram();
        var act = () => AsyncTransform.TransformProgram(ast);
        return act.Should().Throw<SyntaxError>().Which;
    }

    [Fact]
    public void AnAwaitInAnAnnotatedAssignmentIsNamedAsOne()
    {
        var err = Reject("""
            import asyncio
            from pymcu.types import uint16

            async def measure() -> uint16:
                await asyncio.sleep_ms(10)
                return 42

            async def main():
                v: uint16 = await measure()
            """);

        err.Message.Should().Contain("annotated assignment").And.NotContain("VarDecl");
        err.Line.Should().Be(9);
    }

    [Fact]
    public void AnAwaitUsedAsAValueSaysSoRatherThanNamingExprStmt()
    {
        var err = Reject("""
            import asyncio

            async def main():
                print(await asyncio.sleep_ms(1))
            """);

        err.Message.Should().Contain("cannot be used as a value").And.NotContain("ExprStmt");
        err.Line.Should().Be(4);
    }

    [Theory]
    [InlineData("try:\n        await asyncio.sleep_ms(1)\n    except ValueError:\n        pass",
        "`try`/`except`", "TryStmt")]
    [InlineData("with open(\"x\") as f:\n        await asyncio.sleep_ms(1)",
        "`with` block", "WithStmt")]
    public void AnAwaitInAnUnsupportedBlockNamesTheBlockInPython(
        string body, string expected, string astClassName)
    {
        var err = Reject($"""
            import asyncio

            async def main():
                {body}
            """);

        err.Message.Should().Contain(expected).And.NotContain(astClassName);
        err.Line.Should().Be(4);
    }

    [Fact]
    public void AwaitingSomethingOtherThanSleepPointsAtTheAwait()
    {
        var err = Reject("""
            import asyncio

            async def other():
                await asyncio.sleep_ms(1)

            async def main():
                await asyncio.sleep_ms(1)
                await other()
            """);

        err.Message.Should().Contain("only `await asyncio.sleep(n)`");
        // The second await, not the first, and not the top of the file.
        err.Line.Should().Be(8);
    }

    [Fact]
    public void AForOverSomethingOtherThanRangePointsAtTheFor()
    {
        var err = Reject("""
            import asyncio
            from pymcu.types import uint8

            delays: list[uint8] = [10, 20]

            async def main():
                for d in delays:
                    await asyncio.sleep_ms(d)
            """);

        err.Message.Should().Contain("`for i in range(...)`");
        err.Line.Should().Be(7);
    }

    [Fact]
    public void AMissingAsyncioImportPointsAtTheCoroutine()
    {
        var err = Reject("""
            x = 1
            y = 2

            async def main():
                pass
            """);

        err.Message.Should().Contain("never imports asyncio");
        err.Line.Should().Be(4);
    }

    [Fact]
    public void ASleepLongerThanTheCounterPointsAtTheAwait()
    {
        var err = Reject("""
            import asyncio

            async def main():
                await asyncio.sleep_ms(1)
                await asyncio.sleep(5000)
            """);

        err.Message.Should().Contain("4294 seconds");
        err.Line.Should().Be(5);
    }

    [Fact]
    public void CreateTaskInsideALoopPointsAtTheCreateTask()
    {
        var err = Reject("""
            import asyncio
            from pymcu.types import uint8

            async def work():
                await asyncio.sleep_ms(10)

            async def main():
                i: uint8 = 0
                while i < 2:
                    asyncio.create_task(work())
                    i = i + 1
                await asyncio.sleep_ms(100)

            asyncio.run(main())
            """);

        err.Message.Should().Contain("inside a loop");
        err.Line.Should().Be(10);
    }
}
