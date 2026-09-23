using System;
using Xunit;
using FluentAssertions;
using PyMCU.Common;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;

namespace PyMCU.UnitTests;

/// <summary>
/// Generators are supported in one shape: a module-level plain function, consumed by `for`.
/// Every other shape in Python's generator vocabulary is refused, and the refusals used to
/// report as something else entirely -- a message about `for`-in iterable kinds, a mangled
/// symbol the program never wrote, or where the parser stopped. None of those tell the reader
/// that what they wrote is a generator form that does not exist here.
///
/// Each test pins one form. The discriminating assertion is that the message names the
/// construct; the `NotContain` is the invariant that the old wrong message has not come back.
/// </summary>
public class GeneratorSurfaceDiagnosticTests
{
    private static string TransformError(string source)
    {
        var ast = new Parser(new Lexer(source).Tokenize()).ParseProgram();
        var act = () => AsyncTransform.TransformProgram(ast);
        return act.Should().Throw<SyntaxError>().Which.Message;
    }

    // ── a generator written as a method ──────────────────────────────────────────
    // Only `prog.Functions` is scanned for `yield`, so a method never became a generator and
    // `for v in s.items()` fell through to the for-in lowering, which answered with a list of
    // iterable kinds that never mentions generators or methods.

    [Fact]
    public void AGeneratorMethodIsRefusedAtItsDefinition()
    {
        var msg = TransformError("""
            from pymcu.types import uint8

            class Source:
                def __init__(self):
                    self.n: uint8 = 3

                def items(self):
                    i: uint8 = 0
                    while i < self.n:
                        yield i
                        i = i + 1
            """);

        msg.Should().Contain("items").And.Contain("Source").And.Contain("module-level");
        msg.Should().NotContain("for-in loop iterable");
    }

    [Fact]
    public void AGeneratorMethodSaysHowToMoveItOut()
    {
        var msg = TransformError("""
            from pymcu.types import uint8

            class Source:
                def __init__(self):
                    self.n: uint8 = 3

                def items(self):
                    yield self.n
            """);

        // The way out is the same one the coroutine-method refusal offers: take what the body
        // reads from `self` as an argument.
        msg.Should().Contain("argument");
    }

    // ── a generator written as @inline ───────────────────────────────────────────
    // `genFns` excludes @inline, so the body kept its `yield` and the caller's `for` reported
    // the iterable-kind message, naming neither `yield` nor `@inline`.

    [Fact]
    public void AnInlineGeneratorIsRefusedByName()
    {
        var msg = TransformError("""
            @inline
            def gen():
                yield 1
            """);

        msg.Should().Contain("@inline").And.Contain("gen");
        msg.Should().NotContain("for-in loop iterable");
    }

    // ── the generator protocol methods ───────────────────────────────────────────
    // A generator lowers to a class named after the function, so `g.send(1)` resolved to
    // `gen_send` and came back as "call to undefined function 'gen_send'" -- a symbol the
    // reader never typed, with "(typo, or a missing import?)" pointing at neither.
    // These are IR-level and are pinned in IR/GeneratorProtocolDiagnosticTests.

    // ── an async generator ─────────────────────────────────────────────────────
    // `async def` with `yield` is an async generator in Python. The coroutine lowering
    // took it anyway -- it has no channel to publish a yielded value, so the program
    // compiled and the `yield` silently did nothing (driven with asyncio.run, CPython
    // raised TypeError and the emulator printed END). Refused at the yield by name.

    [Fact]
    public void AnAsyncGeneratorIsRefusedAsAnAsyncGenerator()
    {
        var msg = TransformError("""
            import asyncio

            async def ticks():
                yield 1

            async def main():
                pass
            """);

        msg.Should().Contain("async generator");
        msg.Should().NotContain("for-in loop iterable");
    }

    [Fact]
    public void AnAsyncGeneratorDiagnosticMarksTheYield()
    {
        var ast = new Parser(new Lexer("""
            import asyncio

            async def ticks():
                await asyncio.sleep_ms(1)
                yield 1
            """).Tokenize()).ParseProgram();
        var err = Assert.Throws<SyntaxError>(() => AsyncTransform.TransformProgram(ast));

        err.Line.Should().Be(5);
        err.Column.Should().Be(5);
    }

    // ── yields where the splitter cannot cut ───────────────────────────────────
    // ContainsYield only saw a yield that was a whole statement inside Block/if/while/
    // for. A yield inside try/with/match (or a nested def) left the function
    // unclassified, and the caller's `for` fell to the iterable-kind diagnostic, naming
    // neither construct. Each refusal names the container and marks the yield.

    [Fact]
    public void AYieldInsideTryNamesTheTry()
    {
        var ast = new Parser(new Lexer("""
            def g():
                try:
                    x = 1
                    yield x
                except ValueError:
                    pass
            """).Tokenize()).ParseProgram();
        var err = Assert.Throws<SyntaxError>(() => AsyncTransform.TransformProgram(ast));

        err.Message.Should().Contain("`try`");
        err.Line.Should().Be(4);
        err.Column.Should().Be(9);
    }

    [Fact]
    public void AYieldInsideWithNamesTheWith()
    {
        var ast = new Parser(new Lexer("""
            def g():
                with cm():
                    yield 1
            """).Tokenize()).ParseProgram();
        var err = Assert.Throws<SyntaxError>(() => AsyncTransform.TransformProgram(ast));

        err.Message.Should().Contain("`with`");
        err.Line.Should().Be(3);
    }

    [Fact]
    public void AYieldInsideMatchNamesTheMatch()
    {
        var ast = new Parser(new Lexer("""
            def g(k):
                match k:
                    case 1:
                        yield 11
            """).Tokenize()).ParseProgram();
        var err = Assert.Throws<SyntaxError>(() => AsyncTransform.TransformProgram(ast));

        err.Message.Should().Contain("`match`");
        err.Line.Should().Be(4);
    }

    [Fact]
    public void AYieldInsideANestedDefNamesTheNestedFunction()
    {
        var msg = TransformError("""
            def outer():
                @inline
                def inner():
                    yield 1
            """);

        msg.Should().Contain("inner");
        msg.Should().NotContain("for-in loop iterable");
    }

    [Fact]
    public void AYieldInsideAMethodInsideTryIsStillTheMethodRefusal()
    {
        // The deep finder, not ContainsYield: a `try:` around the yield did not hide the
        // fact that what was written is a generator method.
        var msg = TransformError("""
            class A:
                def m(self):
                    try:
                        x = 1
                        yield x
                    except ValueError:
                        pass
            """);

        msg.Should().Contain("method").And.Contain("A");
    }

    // ── a generator expression ───────────────────────────────────────────────────
    // `(x for x in ...)` PARSES now: as the argument of all()/any()/sum()/min()/max() it
    // unrolls over a compile-time sequence. Everywhere else the IR generator refuses it --
    // so the refusal has moved from the parser to IR generation, which is where these pin
    // the wording.

    private static string IRError(string source)
    {
        var ast = new Parser(new Lexer(source).Tokenize()).ParseProgram();
        AsyncTransform.TransformProgram(ast);
        var ex = Assert.ThrowsAny<CompilerError>(
            () => new IRGenerator().Generate(
                ast, new Dictionary<string, ProgramNode>(),
                new DeviceConfig { Arch = "avr" }));
        return ex.Message;
    }

    [Fact]
    public void AGeneratorExpressionIsNamed_NotReportedAsAMissingParen()
    {
        var msg = IRError("""
            def main():
                for v in (x for x in range(3)):
                    print(v)
            """);

        msg.Should().Contain("generator expression");
        msg.Should().NotContain("Expected ')'");
    }

    [Fact]
    public void AGeneratorExpressionPointsAtTheFormThatWorks()
    {
        var msg = IRError("""
            def main():
                for v in (x for x in range(3)):
                    print(v)
            """);

        // The supported shape is the reductions unrolling it at compile time.
        msg.Should().Contain("all()").And.Contain("any()").And.Contain("sum()");
    }
}
