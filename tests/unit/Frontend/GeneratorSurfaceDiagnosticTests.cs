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
    // Phase 1 of RFC 0011: a method that yields lowers to a machine class namespaced under
    // its class (`C.read` -> `C_read`) whose `__init__` keeps the receiver as the first
    // field. The method itself leaves the class body.

    [Fact]
    public void AGeneratorMethodLowersToAMachineClass()
    {
        var ast = new Parser(new Lexer("""
            from pymcu.types import uint8

            class Source:
                def __init__(self):
                    self.n: uint8 = 3

                def items(self):
                    i: uint8 = 0
                    while i < self.n:
                        yield i
                        i = i + 1
            """).Tokenize()).ParseProgram();
        AsyncTransform.TransformProgram(ast);

        var machine = ast.GlobalStatements.OfType<ClassDef>()
            .SingleOrDefault(c => c.Name == "Source_items");
        machine.Should().NotBeNull("the method lowers to a state-machine class of its own");
        machine!.IsGenerator.Should().BeTrue();
        // The method is gone from the class: what remains cannot carry a raw `yield` to IR.
        var cls = ast.GlobalStatements.OfType<ClassDef>().Single(c => c.Name == "Source");
        ((Block)cls.Body).Statements.OfType<FunctionDef>()
            .Should().NotContain(m => m.Name == "items");
    }

    [Fact]
    public void AGeneratorMethodKeepsTheReceiverAsAField()
    {
        var ast = new Parser(new Lexer("""
            class Source:
                def __init__(self):
                    self.n = 3

                def items(self):
                    yield self.n
            """).Tokenize()).ParseProgram();
        AsyncTransform.TransformProgram(ast);

        var machine = ast.GlobalStatements.OfType<ClassDef>()
            .Single(c => c.Name == "Source_items");
        var init = ((Block)machine.Body).Statements.OfType<FunctionDef>()
            .Single(f => f.Name == "__init__");
        // __init__ takes `_recv` as parameter zero -- the receiver -- and stores it as a
        // field, so `self.n` in the body reads the receiver's own slot, not a copy.
        init.Params.Skip(1).First().Name.Should().Be("_recv");
        bool StoresRecv(Statement s) =>
            s is AssignStmt { Target: MemberAccessExpr { Member: "_recv" } };
        Assert.Contains(init.Body.Statements, StoresRecv);
        // No bare `self.n` left inside poll: the receiver reads through `self._recv`.
        var poll = ((Block)machine.Body).Statements.OfType<FunctionDef>()
            .Single(f => f.Name == "poll");
        var bareSelf = AllMembers(poll.Body)
            .Where(m => m.Object is VariableExpr { Name: "self" } && m.Member == "n");
        bareSelf.Should().BeEmpty();
    }

    [Fact]
    public void AForOverAGeneratorMethodConstructsTheMachine()
    {
        var ast = new Parser(new Lexer("""
            class Bag:
                def __init__(self):
                    self.n = 3
                def items(self):
                    i = 0
                    while i < self.n:
                        yield i
                        i = i + 1

            bag = Bag()
            for v in bag.items():
                pass
            """).Tokenize()).ParseProgram();
        AsyncTransform.TransformProgram(ast);

        // `for v in bag.items()` desugars to `__genN = Bag_items(bag)` + the poll loop.
        var assigns = ast.GlobalStatements
            .SelectMany(AllStatements)
            .OfType<AssignStmt>()
            .Where(a => a.Value is CallExpr { Callee: VariableExpr { Name: "Bag_items" } });
        bool PassesTheReceiver(AssignStmt a) =>
            ((CallExpr)a.Value!).Args.Count == 1
            && ((CallExpr)a.Value!).Args[0] is VariableExpr { Name: "bag" };
        Assert.Single(assigns.Where(PassesTheReceiver));
    }

    private static IEnumerable<Statement> AllStatements(Statement s)
    {
        yield return s;
        if (s is Block b)
            foreach (var st in b.Statements)
            foreach (var x in AllStatements(st)) yield return x;
        if (s is WhileStmt w)
            foreach (var x in AllStatements(w.Body)) yield return x;
        if (s is IfStmt i)
        {
            foreach (var x in AllStatements(i.ThenBranch)) yield return x;
            foreach (var (_, eb) in i.ElifBranches)
            foreach (var x in AllStatements(eb)) yield return x;
            if (i.ElseBranch != null)
                foreach (var x in AllStatements(i.ElseBranch)) yield return x;
        }
    }

    private static IEnumerable<MemberAccessExpr> AllMembers(Statement s)
    {
        foreach (var st in AllStatements(s))
        {
            foreach (var e in st switch
            {
                ExprStmt es => new[] { es.Expr },
                AssignStmt a => new Expression[] { a.Target, a.Value },
                ReturnStmt { Value: { } rv } => new[] { rv },
                IfStmt i => new[] { i.Condition },
                WhileStmt w => new[] { w.Condition },
                _ => System.Array.Empty<Expression>(),
            })
            {
                if (e is MemberAccessExpr m)
                {
                    yield return m;
                    if (m.Object is MemberAccessExpr inner) yield return inner;
                }
                if (e is CallExpr { Callee: MemberAccessExpr cm }) yield return cm;
            }
        }
    }

    [Fact]
    public void AGeneratorDunderIsRefusedByName()
    {
        var msg = TransformError("""
            class C:
                def __init__(self):
                    yield 1
            """);

        msg.Should().Contain("__init__").And.Contain("C").And.Contain("dunder");
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
    public void AYieldInsideAMethodInsideTryNamesTheTry()
    {
        // Methods are generators now, so the refusal that survives is the more specific one:
        // the `try` around the yield, not the method holding it.
        var msg = TransformError("""
            class A:
                def m(self):
                    try:
                        x = 1
                        yield x
                    except ValueError:
                        pass
            """);

        msg.Should().Contain("`try`");
    }

    [Fact]
    public void AReturnInsideAKeptIfEndsTheGenerator()
    {
        // `if k: return` contains no yield, so it was kept whole inside the state -- and
        // its bare `return` then reached the IR as `poll -> uint8` returning None, a
        // diagnostic about a function the reader never wrote. A return is a state
        // transition: the splitter takes it wherever a yield could be.
        var ast = new Parser(new Lexer("""
            from pymcu.chips.atmega328p import GPIOR1
            def g(k):
                yield 1
                if k:
                    return
                yield 2
            def main():
                for v in g(1):
                    GPIOR1.value = v
            """).Tokenize()).ParseProgram();
        AsyncTransform.TransformProgram(ast);
        var act = () => new IRGenerator().Generate(
            ast, new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });

        act.Should().NotThrow();
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
