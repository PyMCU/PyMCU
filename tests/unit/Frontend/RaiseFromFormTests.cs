using System;
using System.Collections.Generic;
using System.Linq;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU#434. `raise X(...) from Y` is accepted and compiled as `raise X(...)`.
///
/// There is no traceback and no `__cause__` on this target, so the two are
/// indistinguishable once compiled. Y is parsed (a syntax error inside it is still
/// caught) and discarded, the same treatment a non-call raise MESSAGE already gets
/// (#262). Both front ends discard the same way, so the #277 divergence (one refusing,
/// the other silently building) does not reopen.
/// </summary>
public class RaiseFromFormTests
{
    private static ProgramNode Parse(string src) =>
        new Parser(new Lexer(src).Tokenize()).ParseProgram();

    private static ProgramNode Translate(string src) =>
        PythonAstReader.ParseSource(src, "main.py");

    private static RaiseStmt RaiseOf(ProgramNode prog) =>
        prog.Functions.SelectMany(f => Flatten(f.Body)).OfType<RaiseStmt>()
            .Single(r => r.ErrorType == "TypeError");

    private static IEnumerable<Statement> Flatten(Statement? s)
    {
        if (s == null) yield break;
        yield return s;
        IEnumerable<Statement?> children = s switch
        {
            Block b => b.Statements,
            FunctionDef f => [f.Body],
            TryStmt t => [.. t.Body, .. t.Handlers.SelectMany(h => h.Handler),
                          .. t.ElseBody ?? [], .. t.Finally ?? []],
            _ => [],
        };
        foreach (var c in children)
            foreach (var inner in Flatten(c))
                yield return inner;
    }

    private static string Program(string clause) =>
        "def f(x: uint8) -> uint8:\n" +
        "    if x > 3:\n" +
        "        raise ValueError\n" +
        "    return x\n\n" +
        "def main() -> None:\n" +
        "    prev: uint8 = 0\n" +
        "    try:\n" +
        "        v: uint8 = f(9)\n" +
        "    except ValueError:\n" +
        $"        {clause}\n";

    [Theory]
    [InlineData("raise TypeError(\"wrapped\") from None")]
    [InlineData("raise TypeError(\"wrapped\") from prev")]
    [InlineData("raise TypeError from prev")]
    public void BothFrontEndsAcceptRaiseFrom(string clause)
    {
        Assert.NotNull(Parse(Program(clause)));
        Assert.NotNull(Translate(Program(clause)));
    }

    [Theory]
    [InlineData("raise TypeError(\"wrapped\") from None", "raise TypeError(\"wrapped\")")]
    [InlineData("raise TypeError(\"wrapped\") from prev", "raise TypeError(\"wrapped\")")]
    [InlineData("raise TypeError from prev", "raise TypeError")]
    public void TheAstIsTheSameAsTheRaiseWithoutFrom(string withFrom, string without)
    {
        var a = RaiseOf(Parse(Program(withFrom)));
        var b = RaiseOf(Parse(Program(without)));
        Assert.Equal(b.ErrorType, a.ErrorType);
        Assert.Equal(b.Message, a.Message);
        Assert.Equal(b.MessageName, a.MessageName);

        var ta = RaiseOf(Translate(Program(withFrom)));
        var tb = RaiseOf(Translate(Program(without)));
        Assert.Equal(tb.ErrorType, ta.ErrorType);
        Assert.Equal(tb.Message, ta.Message);
        Assert.Equal(tb.MessageName, ta.MessageName);
    }

    [Fact]
    public void AnUndefinedNameAfterFromIsAccepted()
    {
        const string src =
            "def main() -> None:\n" +
            "    raise RuntimeError from totally_undefined_name_xyz\n";
        Assert.NotNull(Parse(src));
        Assert.NotNull(Translate(src));
    }

    [Fact]
    public void CauseDundersOnABoundExceptionAreRefusedByName()
    {
        const string src =
            "def f() -> None:\n" +
            "    raise ValueError(\"boom\")\n" +
            "def main() -> None:\n" +
            "    try:\n" +
            "        f()\n" +
            "    except ValueError as e:\n" +
            "        x = e.__cause__\n";

        string Msg(ProgramNode p) =>
            Assert.ThrowsAny<Exception>(() => new IRGenerator().Generate(
                p, new Dictionary<string, ProgramNode>(),
                new DeviceConfig { Arch = "avr" })).Message;

        foreach (var ast in new[] { Parse(src), Translate(src) })
        {
            var msg = Msg(ast);
            Assert.Contains("__cause__", msg);
            Assert.Contains("chain", msg);
        }
    }

    [Fact]
    public void ContextDunderIsRefusedTheSameWay()
    {
        const string src =
            "def f() -> None:\n" +
            "    raise ValueError(\"boom\")\n" +
            "def main() -> None:\n" +
            "    try:\n" +
            "        f()\n" +
            "    except ValueError as e:\n" +
            "        x = e.__context__\n";

        var msg = Assert.ThrowsAny<Exception>(() => new IRGenerator().Generate(
            Parse(src), new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" })).Message;
        Assert.Contains("__context__", msg);
        Assert.Contains("chain", msg);
    }
}
