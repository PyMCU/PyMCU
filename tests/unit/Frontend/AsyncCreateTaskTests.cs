using Xunit;
using FluentAssertions;
using PyMCU.Common;
using PyMCU.Frontend;

namespace PyMCU.UnitTests;

/// <summary>
/// `create_task` builds a task set that is fixed at compile time: a coroutine lowers to its
/// own ZCA type, so there is no common runtime handle to append to a list, but the set of
/// tasks an embedded program starts is knowable while compiling it. Each call site gets a
/// global instance built at module level and a flag the generated scheduler polls. These
/// tests pin the shape and, mostly, the four things that shape cannot express, because each
/// one is a program that would otherwise build and quietly do something else.
/// </summary>
public class AsyncCreateTaskTests
{
    private static ProgramNode Lower(string source)
    {
        var ast = new Parser(new Lexer(source).Tokenize()).ParseProgram();
        AsyncTransform.TransformProgram(ast);
        return ast;
    }

    private const string Worker = """
        import asyncio

        async def work():
            await asyncio.sleep_ms(10)

        """;

    [Fact]
    public void EachCallSiteBecomesItsOwnGlobalInstance()
    {
        var ast = Lower(Worker + """
            async def main():
                asyncio.create_task(work())
                asyncio.create_task(work())
                await asyncio.sleep_ms(100)

            asyncio.run(main())
            """);

        var assigned = ast.GlobalStatements.OfType<AssignStmt>()
            .Select(a => a.Target).OfType<VariableExpr>().Select(v => v.Name).ToList();

        // Two call sites of the same coroutine are two tasks, each with its own instance
        // and its own flag; they share only the poll() subroutine.
        assigned.Should().Contain(new[]
        {
            "__pymcu_task0", "__pymcu_task0_on",
            "__pymcu_task1", "__pymcu_task1_on",
        });
    }

    [Fact]
    public void CreateTaskInALoopIsRefused()
    {
        // One call site is one task, so a loop would start one task and not one per turn.
        var inLoop = () => Lower(Worker + """
            async def main():
                i: uint8 = 0
                while i < 2:
                    asyncio.create_task(work())
                    i = i + 1
                await asyncio.sleep_ms(100)

            asyncio.run(main())
            """);

        inLoop.Should().Throw<SyntaxError>().WithMessage("*inside a loop*not one per iteration*");
    }

    [Fact]
    public void CreateTaskAfterAnAwaitIsRefused()
    {
        // Every task starts when run() does, so a create_task past a suspension point would
        // have started earlier than the program says.
        var late = () => Lower(Worker + """
            async def main():
                await asyncio.sleep_ms(1)
                asyncio.create_task(work())
                await asyncio.sleep_ms(100)

            asyncio.run(main())
            """);

        late.Should().Throw<SyntaxError>().WithMessage("*after an `await`*");
    }

    [Fact]
    public void CreateTaskOfSomethingThatIsNotACoroutineIsRefused()
    {
        var notCoro = () => Lower("""
            import asyncio

            def work():
                pass

            async def main():
                asyncio.create_task(work())
                await asyncio.sleep_ms(1)

            asyncio.run(main())
            """);

        notCoro.Should().Throw<SyntaxError>().WithMessage("*takes a coroutine of this module*");
    }

    [Fact]
    public void ARunTimeArgumentIsRefused()
    {
        // The instance is built at module level, so its arguments are evaluated there.
        var runtimeArg = () => Lower("""
            import asyncio
            from pymcu.types import uint16

            async def work(n: uint16):
                await asyncio.sleep_ms(n)

            async def main(m: uint16):
                asyncio.create_task(work(m))
                await asyncio.sleep_ms(1)

            asyncio.run(main(5))
            """);

        runtimeArg.Should().Throw<SyntaxError>().WithMessage("*compile-time constant arguments*");
    }

    [Fact]
    public void CreateTaskWithoutRunIsRefused()
    {
        // The scheduler is generated in place of run(); with no run() nothing polls.
        var noRun = () => Lower(Worker + """
            async def main():
                asyncio.create_task(work())
                await asyncio.sleep_ms(1)

            m = main()
            while m.poll() == 1:
                pass
            """);

        noRun.Should().Throw<SyntaxError>().WithMessage("*needs a*run(main())*");
    }

    [Fact]
    public void AConstantArgumentIsAccepted()
    {
        var ast = Lower("""
            import asyncio
            from pymcu.types import uint16

            async def work(n: uint16):
                await asyncio.sleep_ms(n)

            async def main():
                asyncio.create_task(work(10 * 2))
                await asyncio.sleep_ms(100)

            asyncio.run(main())
            """);

        var seed = ast.GlobalStatements.OfType<AssignStmt>()
            .Single(a => a.Target is VariableExpr { Name: "__pymcu_task0" });
        ((CallExpr)seed.Value).Args.Should().ContainSingle();
    }
}
