using Xunit;
using FluentAssertions;
using PyMCU.Common;
using PyMCU.Frontend;

namespace PyMCU.UnitTests;

/// <summary>
/// A coroutine has to be a module-level function. Written as a method it used to reach the IR
/// generator untransformed, because the desugar only ever looked at module-level functions,
/// and came back as "`await` is only valid inside an `async def`" -- a message about a rule the
/// program already obeys, pointed at the call site rather than the definition.
/// </summary>
public class AsyncMethodTests
{
    private static SyntaxError Reject(string source)
    {
        var ast = new Parser(new Lexer(source).Tokenize()).ParseProgram();
        var act = () => AsyncTransform.TransformProgram(ast);
        return act.Should().Throw<SyntaxError>().Which;
    }

    [Fact]
    public void ACoroutineMethodIsRefusedAtItsDefinition()
    {
        var err = Reject("""
            import asyncio
            from pymcu.types import uint16

            class Beeper:
                def __init__(self, ms: uint16):
                    self.ms: uint16 = ms

                async def run(self):
                    await asyncio.sleep_ms(self.ms)

            b = Beeper(200)
            asyncio.run(b.run())
            """);

        // Names the method, its class, and the rule -- not `await`, which the program obeys.
        err.Message.Should().Contain("`async def run` is a method of class 'Beeper'")
            .And.Contain("module-level function");
        err.Message.Should().NotContain("only valid inside an `async def`");
        // The definition, not the `asyncio.run(b.run())` four lines below it.
        err.Line.Should().Be(8);
    }

    [Fact]
    public void TheSuggestedRewriteCarriesTheInstanceAndTheOtherParameters()
    {
        var err = Reject("""
            import asyncio
            from pymcu.types import uint16

            class Motor:
                async def spin(self, speed: uint16, turns: uint16):
                    await asyncio.sleep_ms(speed)

            asyncio.run(Motor().spin(10, 2))
            """);

        err.Message.Should().Contain("`async def spin(motor, speed, turns)`");
    }

    [Fact]
    public void APlainMethodInTheSameClassIsUntouched()
    {
        // Only `async def` is refused; an ordinary method next to a module-level coroutine
        // still lowers normally.
        var ast = new Parser(new Lexer("""
            import asyncio
            from pymcu.types import uint16

            class Beeper:
                def __init__(self, ms: uint16):
                    self.ms: uint16 = ms

                def period(self) -> uint16:
                    return self.ms

            async def run(beeper: Beeper):
                await asyncio.sleep_ms(10)

            asyncio.run(run(Beeper(200)))
            """).Tokenize()).ParseProgram();

        var act = () => AsyncTransform.TransformProgram(ast);
        act.Should().NotThrow();
        ast.GlobalStatements.OfType<ClassDef>().Should().Contain(c => c.Name == "run");
    }
}
