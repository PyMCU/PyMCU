using Xunit;
using FluentAssertions;
using PyMCU.Common;
using PyMCU.Frontend;

namespace PyMCU.UnitTests;

/// <summary>
/// `gather` drives its two coroutines to completion before it returns, so a gather passed as an
/// argument to another one is not a way to reach three tasks: the inner one finishes before the
/// outer one starts. That is sequential, and it is what gather's own docstring used to advise.
///
/// Measured on the simulator before writing this: two gathers in a row print
/// `3 2 3 2` then `1 1 1 1`, with no interleaving between the pairs.
///
/// Until now it reached the inliner instead, which reported `gather` as recursive because the
/// same @inline name appeared twice in one expansion. gather does not call itself, so that
/// message described a property the program does not have, and its advice, rewrite it as a
/// loop, cannot be applied to a library function.
/// </summary>
public class AsyncNestedGatherTests
{
    private const string Coroutines = """
        import asyncio

        async def t1():
            await asyncio.sleep_ms(100)

        async def t2():
            await asyncio.sleep_ms(200)

        async def t3():
            await asyncio.sleep_ms(300)

        """;

    private static SyntaxError Reject(string tail)
    {
        var ast = new Parser(new Lexer(Coroutines + tail).Tokenize()).ParseProgram();
        var act = () => AsyncTransform.TransformProgram(ast);
        return act.Should().Throw<SyntaxError>().Which;
    }

    [Theory]
    [InlineData("asyncio.gather(t1(), asyncio.gather(t2(), t3()))")]
    [InlineData("asyncio.gather(asyncio.gather(t1(), t2()), t3())")]
    public void ANestedGatherIsRefusedByName(string call)
    {
        var err = Reject(call);

        // Names the construct and the way out, and says why nesting cannot work here.
        err.Message.Should().Contain("inside another")
            .And.Contain("create_task")
            .And.Contain("completion before it returns");
        // The message the inliner used to give described a property gather does not have.
        err.Message.Should().NotContain("recursive");
        err.Line.Should().Be(11);
    }

    [Fact]
    public void APlainGatherIsUntouched()
    {
        var ast = new Parser(new Lexer(Coroutines + "asyncio.gather(t1(), t2())").Tokenize())
            .ParseProgram();

        var act = () => AsyncTransform.TransformProgram(ast);
        act.Should().NotThrow();
    }

    [Fact]
    public void AGatherBesideAnotherGatherIsFine()
    {
        // Two gathers in sequence are sequential and that is what they say they are; only one
        // INSIDE another is refused.
        var ast = new Parser(new Lexer(Coroutines + """
            asyncio.gather(t1(), t2())
            asyncio.gather(t3(), t3())
            """).Tokenize()).ParseProgram();

        var act = () => AsyncTransform.TransformProgram(ast);
        act.Should().NotThrow();
    }
}
