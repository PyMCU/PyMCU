using System;
using Xunit;
using FluentAssertions;
using PyMCU.Common;
using PyMCU.Frontend;

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

    private static string ParseError(string source)
        => Assert.ThrowsAny<Exception>(
            () => new Parser(new Lexer(source).Tokenize()).ParseProgram()).Message;

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

    // ── a generator expression ───────────────────────────────────────────────────

    [Fact]
    public void AGeneratorExpressionIsNamed_NotReportedAsAMissingParen()
    {
        var msg = ParseError("""
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
        var msg = ParseError("""
            def main():
                for v in (x for x in range(3)):
                    print(v)
            """);

        // A generator function plus `for` is the supported shape, and a plain `for` covers
        // this particular one outright.
        msg.Should().Contain("for");
    }
}
