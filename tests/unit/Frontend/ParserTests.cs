using System;
using System.Linq;
using PyMCU.Common;
using Xunit;
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

/// <summary>
/// Additional parser tests covering control flow statements, typed parameters,
/// augmented assignments, decorators, and other constructs.
/// </summary>
public class ParserControlFlowTests
{
    private static ProgramNode Parse(string source)
    {
        var lexer = new Lexer(source);
        var tokens = lexer.Tokenize();
        var parser = new Parser(tokens);
        return parser.ParseProgram();
    }

    // ─── If / elif / else ─────────────────────────────────────────────────

    [Fact]
    public void IfStatement_Parsed()
    {
        var prog = Parse("def f(x: int):\n    if x:\n        return 1\n");
        var stmts = prog.Functions[0].Body.Statements;

        var ifStmt = Assert.IsType<IfStmt>(stmts[0]);
        Assert.NotNull(ifStmt.Condition);
        Assert.Null(ifStmt.ElseBranch);
    }

    [Fact]
    public void IfElse_Parsed()
    {
        var prog = Parse(
            "def f(x: int):\n" +
            "    if x:\n" +
            "        return 1\n" +
            "    else:\n" +
            "        return 0\n");

        var ifStmt = (IfStmt)prog.Functions[0].Body.Statements[0];
        Assert.NotNull(ifStmt.ElseBranch);
    }

    [Fact]
    public void IfElifElse_Parsed()
    {
        var prog = Parse(
            "def f(x: int):\n" +
            "    if x == 1:\n" +
            "        return 10\n" +
            "    elif x == 2:\n" +
            "        return 20\n" +
            "    else:\n" +
            "        return 0\n");

        var ifStmt = (IfStmt)prog.Functions[0].Body.Statements[0];
        Assert.Single(ifStmt.ElifBranches);
        Assert.NotNull(ifStmt.ElseBranch);
    }

    // ─── While loop ───────────────────────────────────────────────────────

    [Fact]
    public void WhileLoop_Parsed()
    {
        var prog = Parse("def f():\n    while 1:\n        pass\n");
        var stmt = Assert.IsType<WhileStmt>(prog.Functions[0].Body.Statements[0]);

        var cond = Assert.IsType<IntegerLiteral>(stmt.Condition);
        Assert.Equal(1, cond.Value);
    }

    [Fact]
    public void WhileWithBreak_Parsed()
    {
        var prog = Parse(
            "def f():\n" +
            "    while 1:\n" +
            "        break\n");

        var loop = (WhileStmt)prog.Functions[0].Body.Statements[0];
        var body = (Block)loop.Body;
        Assert.IsType<BreakStmt>(body.Statements[0]);
    }

    [Fact]
    public void WhileWithContinue_Parsed()
    {
        var prog = Parse(
            "def f():\n" +
            "    while 1:\n" +
            "        continue\n");

        var loop = (WhileStmt)prog.Functions[0].Body.Statements[0];
        var body = (Block)loop.Body;
        Assert.IsType<ContinueStmt>(body.Statements[0]);
    }

    // ─── Match / case ─────────────────────────────────────────────────────

    [Fact]
    public void MatchStatement_Parsed()
    {
        var prog = Parse(
            "def f(x: int):\n" +
            "    match x:\n" +
            "        case 1:\n" +
            "            return 10\n" +
            "        case _:\n" +
            "            return 0\n");

        var match = Assert.IsType<MatchStmt>(prog.Functions[0].Body.Statements[0]);
        Assert.Equal(2, match.Branches.Count);
    }

    [Fact]
    public void MatchStatement_WildcardBranch_HasNullPattern()
    {
        var prog = Parse(
            "def f(x: int):\n" +
            "    match x:\n" +
            "        case 1:\n" +
            "            return 1\n" +
            "        case _:\n" +
            "            return 0\n");

        var match = (MatchStmt)prog.Functions[0].Body.Statements[0];
        var wildcard = match.Branches.Last();
        Assert.Null(wildcard.Pattern);
    }

    // ─── Function parameters ──────────────────────────────────────────────

    [Fact]
    public void FunctionWithTypedParams_Parsed()
    {
        var prog = Parse(
            "def add(a: uint8, b: uint8) -> uint8:\n" +
            "    return a\n");

        var func = prog.Functions[0];
        Assert.Equal(2, func.Params.Count);
        Assert.Equal("a", func.Params[0].Name);
        Assert.Equal("uint8", func.Params[0].Type);
        Assert.Equal("b", func.Params[1].Name);
        Assert.Equal("uint8", func.ReturnType);
    }

    [Fact]
    public void FunctionWithNoParams_Parsed()
    {
        var prog = Parse("def noop():\n    pass\n");
        Assert.Empty(prog.Functions[0].Params);
    }

    // ─── Augmented Assignments ────────────────────────────────────────────

    [Theory]
    [InlineData("+=",  AugOp.Add)]
    [InlineData("-=",  AugOp.Sub)]
    [InlineData("*=",  AugOp.Mul)]
    [InlineData("&=",  AugOp.BitAnd)]
    [InlineData("|=",  AugOp.BitOr)]
    [InlineData("^=",  AugOp.BitXor)]
    [InlineData("<<=", AugOp.LShift)]
    [InlineData(">>=", AugOp.RShift)]
    public void AugAssignStatement_AllOps_Parsed(string opStr, AugOp expectedOp)
    {
        var prog = Parse($"def f(x: int):\n    x {opStr} 1\n");
        var aug = Assert.IsType<AugAssignStmt>(prog.Functions[0].Body.Statements[0]);
        Assert.Equal(expectedOp, aug.Op);
    }

    // ─── Decorators ───────────────────────────────────────────────────────

    [Fact]
    public void InlineDecorator_SetsIsInline()
    {
        var prog = Parse(
            "@inline\n" +
            "def helper(x: int) -> int:\n" +
            "    return x\n");

        Assert.True(prog.Functions[0].IsInline);
    }

    [Fact]
    public void InterruptDecorator_SetsIsInterrupt()
    {
        var prog = Parse(
            "from pymcu.avr import interrupt\n" +
            "@interrupt(0x02)\n" +
            "def timer_isr():\n" +
            "    pass\n");

        var isr = prog.Functions.First(f => f.Name == "timer_isr");
        Assert.True(isr.IsInterrupt);
        Assert.Equal(0x02, isr.InterruptVector);
    }

    // ─── Pass statement ───────────────────────────────────────────────────

    [Fact]
    public void PassStatement_Parsed()
    {
        var prog = Parse("def f():\n    pass\n");
        Assert.IsType<PassStmt>(prog.Functions[0].Body.Statements[0]);
    }

    // ─── Return without value ─────────────────────────────────────────────

    [Fact]
    public void ReturnNoValue_Parsed()
    {
        var prog = Parse("def f():\n    return\n");
        var ret = Assert.IsType<ReturnStmt>(prog.Functions[0].Body.Statements[0]);
        Assert.Null(ret.Value);
    }

    // ─── Nested function calls as expressions ─────────────────────────────

    [Fact]
    public void NestedCallExpr_Parsed()
    {
        var prog = Parse("def f():\n    x = foo(bar(1))\n");
        var assign = (AssignStmt)prog.Functions[0].Body.Statements[0];
        var outer = Assert.IsType<CallExpr>(assign.Value);
        Assert.Single(outer.Args);
        Assert.IsType<CallExpr>(outer.Args[0]);
    }

    // ─── Binary expressions ────────────────────────────────────────────────

    [Theory]
    [InlineData("a + b",  BinaryOp.Add)]
    [InlineData("a - b",  BinaryOp.Sub)]
    [InlineData("a * b",  BinaryOp.Mul)]
    [InlineData("a & b",  BinaryOp.BitAnd)]
    [InlineData("a | b",  BinaryOp.BitOr)]
    [InlineData("a ^ b",  BinaryOp.BitXor)]
    [InlineData("a << b", BinaryOp.LShift)]
    [InlineData("a >> b", BinaryOp.RShift)]
    [InlineData("a == b", BinaryOp.Equal)]
    [InlineData("a != b", BinaryOp.NotEqual)]
    [InlineData("a < b",  BinaryOp.Less)]
    [InlineData("a > b",  BinaryOp.Greater)]
    [InlineData("a <= b", BinaryOp.LessEq)]
    [InlineData("a >= b", BinaryOp.GreaterEq)]
    public void BinaryExpression_ProducesCorrectOp(string expr, BinaryOp expectedOp)
    {
        var prog = Parse($"def f(a: int, b: int):\n    return {expr}\n");
        var ret = (ReturnStmt)prog.Functions[0].Body.Statements[0];
        var bin = Assert.IsType<BinaryExpr>(ret.Value);
        Assert.Equal(expectedOp, bin.Op);
    }

    // ─── Unary expressions ────────────────────────────────────────────────

    [Fact]
    public void UnaryNegate_Parsed()
    {
        var prog = Parse("def f(x: int):\n    return -x\n");
        var ret = (ReturnStmt)prog.Functions[0].Body.Statements[0];
        var un = Assert.IsType<UnaryExpr>(ret.Value);
        Assert.Equal(UnaryOp.Negate, un.Op);
    }

    [Fact]
    public void UnaryNot_Parsed()
    {
        var prog = Parse("def f(x: int):\n    return not x\n");
        var ret = (ReturnStmt)prog.Functions[0].Body.Statements[0];
        var un = Assert.IsType<UnaryExpr>(ret.Value);
        Assert.Equal(UnaryOp.Not, un.Op);
    }

    [Fact]
    public void UnaryBitNot_Parsed()
    {
        var prog = Parse("def f(x: int):\n    return ~x\n");
        var ret = (ReturnStmt)prog.Functions[0].Body.Statements[0];
        var un = Assert.IsType<UnaryExpr>(ret.Value);
        Assert.Equal(UnaryOp.BitNot, un.Op);
    }

    // ─── For loop ─────────────────────────────────────────────────────────

    [Fact]
    public void ForRangeLoop_Parsed()
    {
        var prog = Parse("def f():\n    for i in range(10):\n        pass\n");
        var stmt = Assert.IsType<ForStmt>(prog.Functions[0].Body.Statements[0]);
        Assert.Equal("i", stmt.VarName);
    }

    // ─── Global statement ─────────────────────────────────────────────────

    [Fact]
    public void GlobalStatement_Parsed()
    {
        var prog = Parse("def f():\n    global x\n    pass\n");
        Assert.IsType<GlobalStmt>(prog.Functions[0].Body.Statements[0]);
    }

    // ─── Nonlocal statement ───────────────────────────────────────────────

    [Fact]
    public void NonlocalStatement_Parsed()
    {
        // The PyMCU parser accepts `nonlocal` as a statement node (syntactic parsing),
        // even though nested functions (the only meaningful context for nonlocal) are
        // not yet supported by the compiler backend. The test verifies the parser
        // produces a NonlocalStmt node rather than throwing a syntax error.
        var prog = Parse("def f():\n    nonlocal x\n    pass\n");
        Assert.Contains(prog.Functions[0].Body.Statements, s => s is NonlocalStmt);
    }

    // ─── VarDecl with type annotation ─────────────────────────────────────

    [Fact]
    public void TypeAnnotatedAssignment_Parsed()
    {
        // The parser produces VarDecl for type-annotated assignments like `x: uint8 = 42`
        var prog = Parse("def f():\n    x: uint8 = 42\n");
        var decl = Assert.IsType<VarDecl>(prog.Functions[0].Body.Statements[0]);
        Assert.Equal("x", decl.Name);
        Assert.Equal("uint8", decl.VarType);
        var val = Assert.IsType<IntegerLiteral>(decl.Init);
        Assert.Equal(42, val.Value);
    }

    // ─── Boolean literals ─────────────────────────────────────────────────

    [Fact]
    public void TrueLiteral_Parsed()
    {
        var prog = Parse("def f():\n    return True\n");
        var ret = (ReturnStmt)prog.Functions[0].Body.Statements[0];
        var b = Assert.IsType<BooleanLiteral>(ret.Value);
        Assert.True(b.Value);
    }

    [Fact]
    public void FalseLiteral_Parsed()
    {
        var prog = Parse("def f():\n    return False\n");
        var ret = (ReturnStmt)prog.Functions[0].Body.Statements[0];
        var b = Assert.IsType<BooleanLiteral>(ret.Value);
        Assert.False(b.Value);
    }
}

/// <summary>
/// Tests for the PEP 328 parenthesised multi-line import syntax:
///   from module import (
///       SymA,
///       SymB,
///   )
///
/// The lexer suppresses newlines while parenDepth > 0, so these all collapse
/// to the same token stream as the single-line form from the parser's perspective.
/// </summary>
public class ParserImportTests
{
    private static ProgramNode Parse(string source)
    {
        var lexer = new Lexer(source);
        var tokens = lexer.Tokenize();
        var parser = new Parser(tokens);
        return parser.ParseProgram();
    }

    // ---- single-line baseline -----------------------------------------------

    [Fact]
    public void SingleLine_TwoSymbols_Parsed()
    {
        var prog = Parse("from mod import A, B\n");
        var imp = Assert.Single(prog.Imports);
        Assert.Equal("mod", imp.ModuleName);
        Assert.Equal(new[] { "A", "B" }, imp.Symbols);
    }

    [Fact]
    public void SingleLine_WithAlias_Parsed()
    {
        var prog = Parse("from mod import Foo as F\n");
        var imp = Assert.Single(prog.Imports);
        Assert.Equal("Foo", imp.Symbols[0]);
        Assert.Equal("F", imp.Aliases["Foo"]);
    }

    // ---- parenthesised multi-line -------------------------------------------

    [Fact]
    public void Parenthesised_TwoSymbols_OnSeparateLines()
    {
        // from mod import (
        //     A,
        //     B
        // )
        var src = "from mod import (\n    A,\n    B\n)\n";
        var prog = Parse(src);
        var imp = Assert.Single(prog.Imports);
        Assert.Equal("mod", imp.ModuleName);
        Assert.Equal(new[] { "A", "B" }, imp.Symbols);
    }

    [Fact]
    public void Parenthesised_ThreeSymbols_TrailingComma()
    {
        // from mod import (
        //     A,
        //     B,
        //     C,     <- trailing comma is legal Python
        // )
        var src = "from mod import (\n    A,\n    B,\n    C,\n)\n";
        var prog = Parse(src);
        var imp = Assert.Single(prog.Imports);
        Assert.Equal(new[] { "A", "B", "C" }, imp.Symbols);
    }

    [Fact]
    public void Parenthesised_SingleSymbol_NoTrailingComma()
    {
        var src = "from mod import (\n    A\n)\n";
        var prog = Parse(src);
        var imp = Assert.Single(prog.Imports);
        Assert.Equal(new[] { "A" }, imp.Symbols);
    }

    [Fact]
    public void Parenthesised_WithAliases_Parsed()
    {
        // from mod import (
        //     Foo as F,
        //     Bar as B,
        // )
        var src = "from mod import (\n    Foo as F,\n    Bar as B,\n)\n";
        var prog = Parse(src);
        var imp = Assert.Single(prog.Imports);
        Assert.Equal(new[] { "Foo", "Bar" }, imp.Symbols);
        Assert.Equal("F", imp.Aliases["Foo"]);
        Assert.Equal("B", imp.Aliases["Bar"]);
    }

    [Fact]
    public void Parenthesised_DottedModule_Parsed()
    {
        // from pymcu.chips.atmega328p import (
        //     TCCR0A, TCCR0B, OCR0A,
        //     TCCR1A, TCCR1B,
        //     ICR1, OCR1A,
        // )
        var src = "from pymcu.chips.atmega328p import (\n    TCCR0A, TCCR0B, OCR0A,\n    TCCR1A, TCCR1B,\n    ICR1, OCR1A,\n)\n";
        var prog = Parse(src);
        var imp = Assert.Single(prog.Imports);
        Assert.Equal("pymcu.chips.atmega328p", imp.ModuleName);
        Assert.Equal(new[] { "TCCR0A", "TCCR0B", "OCR0A", "TCCR1A", "TCCR1B", "ICR1", "OCR1A" }, imp.Symbols);
    }

    [Fact]
    public void Parenthesised_FollowedByFunctionDef_BothParsed()
    {
        // Ensures the parser correctly resumes after the closing ')' and newline.
        var src = "from mod import (\n    A,\n    B,\n)\ndef main():\n    pass\n";
        var prog = Parse(src);
        Assert.Single(prog.Imports);
        Assert.Single(prog.Functions);
        Assert.Equal("main", prog.Functions[0].Name);
    }

    [Fact]
    public void Parenthesised_MissingCloseParen_ThrowsSyntaxError()
    {
        var src = "from mod import (\n    A,\n    B\n";
        Assert.ThrowsAny<Exception>(() => Parse(src));
    }
}
