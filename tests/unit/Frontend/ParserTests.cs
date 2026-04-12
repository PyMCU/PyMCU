using System;
using System.Linq;
using PyMCU.Common;
using Xunit;
using PyMCU.Compiler;
using PyMCU.Frontend;

namespace PyMCU.UnitTests;

public class ParserTests
{
    private static ProgramNode Parse(string source)
    {
        var lexer = new Lexer(source);
        var tokens = lexer.Tokenize();
        var parser = new Parser(tokens);
        return parser.ParseProgram();
    }

    [Fact]
    public void EmptyProgram()
    {
        var prog = Parse("");
        Assert.Empty(prog.Functions);
    }

    [Fact]
    public void SingleFunction()
    {
        var prog = Parse("def main():" + (char)10 + "    return 42");
        Assert.Single(prog.Functions);
        Assert.Equal("main", prog.Functions[0].Name);
        Assert.Single(prog.Functions[0].Body.Statements);

        var returnStmt = prog.Functions[0].Body.Statements[0] as ReturnStmt;
        Assert.NotNull(returnStmt);

        var numExpr = returnStmt.Value as IntegerLiteral;
        Assert.NotNull(numExpr);
        Assert.Equal(42, numExpr.Value);
    }

    [Fact]
    public void MultipleFunctions()
    {
        var prog = Parse("def a():" + (char)10 + "    return 1" + (char)10 + (char)10 + "def b():" + (char)10 + "    return 2");
        Assert.Equal(2, prog.Functions.Count);
        Assert.Equal("a", prog.Functions[0].Name);
        Assert.Equal("b", prog.Functions[1].Name);
    }

    [Fact]
    public void NumberBases()
    {
        var prog = Parse("def f():" + (char)10 + "    return 0x10" + (char)10 + "    return 0b10" + (char)10 + "    return 0o10" + (char)10 + "    return 1_000");
        Assert.Single(prog.Functions);
        var statements = prog.Functions[0].Body.Statements;
        Assert.Equal(4, statements.Count);

        Assert.Equal(16, ((IntegerLiteral)((ReturnStmt)statements[0]).Value!).Value);
        Assert.Equal(2, ((IntegerLiteral)((ReturnStmt)statements[1]).Value!).Value);
        Assert.Equal(8, ((IntegerLiteral)((ReturnStmt)statements[2]).Value!).Value);
        Assert.Equal(1000, ((IntegerLiteral)((ReturnStmt)statements[3]).Value!).Value);
    }

    [Fact]
    public void SyntaxError()
    {
        Assert.Throws<SyntaxError>(() => Parse("def main("));
        Assert.Throws<SyntaxError>(() => Parse("def main(): return 1"));
    }

    [Fact]
    public void IndentationError()
    {
        Assert.Throws<IndentationError>(() => Parse("def main():" + (char)10 + "return 1"));
    }

    [Fact]
    public void NestedBlocksAreNotSupportedYet()
    {
        Assert.Throws<SyntaxError>(() => Parse("def a():" + (char)10 + "    def b():" + (char)10 + "        return 1"));
    }
}
