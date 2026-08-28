/*
 * -----------------------------------------------------------------------------
 * PyMCU Compiler (pymcuc)
 * Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
 *
 * SPDX-License-Identifier: MIT
 *
 * -----------------------------------------------------------------------------
 * SAFETY WARNING / HIGH RISK ACTIVITIES:
 * THE SOFTWARE IS NOT DESIGNED, MANUFACTURED, OR INTENDED FOR USE IN HAZARDOUS
 * ENVIRONMENTS REQUIRING FAIL-SAFE PERFORMANCE, SUCH AS IN THE OPERATION OF
 * NUCLEAR FACILITIES, AIRCRAFT NAVIGATION OR COMMUNICATION SYSTEMS, AIR
 * TRAFFIC CONTROL, DIRECT LIFE SUPPORT MACHINES, OR WEAPONS SYSTEMS.
 * -----------------------------------------------------------------------------
 */

using Xunit;
using PyMCU.Frontend;

namespace PyMCU.UnitTests;

/// <summary>
/// A binary expression is located at its OPERATOR, and the diagnostics about it point there.
///
/// Not at the start of the whole expression. `b: uint8 = a // 0` is a division by zero, and
/// the left operand is innocent: a caret under `a` names the wrong thing. The operator is what
/// the message is about, which is also what clang and rustc mark.
/// </summary>
public class BinaryExprPositionTests
{
    private static BinaryExpr FirstBinary(string src)
    {
        var prog = new Parser(new Lexer(src).Tokenize()).ParseProgram();
        var stmt = Assert.IsType<Block>(prog.Functions[0].Body).Statements[0];
        Expression init = stmt switch
        {
            VarDecl v => v.Init!,
            AnnAssign a => a.Value!,
            AssignStmt s => s.Value,
            ExprStmt e => e.Expr,
            _ => throw new Xunit.Sdk.XunitException($"unexpected statement {stmt.GetType().Name}"),
        };
        return Assert.IsType<BinaryExpr>(init);
    }

    [Theory]
    //        1234567890123456789
    [InlineData("    b = a // 0\n", 11)]      // the '//'
    [InlineData("    b = a + 1\n", 11)]       // the '+'
    [InlineData("    b = a * 2\n", 11)]       // the '*'
    [InlineData("    b = a << 3\n", 11)]      // the '<<'
    [InlineData("    b = a & 7\n", 11)]       // the '&'
    [InlineData("    b = a | 7\n", 11)]       // the '|'
    [InlineData("    b = a ^ 7\n", 11)]       // the '^'
    [InlineData("    b = a ** 2\n", 11)]      // the '**'
    [InlineData("    b = a == 2\n", 11)]      // the '=='
    [InlineData("    b = a and 1\n", 11)]     // the 'and'
    [InlineData("    b = a or 1\n", 11)]      // the 'or'
    public void ABinaryExpressionIsLocatedAtItsOperator(string body, int column)
    {
        var e = FirstBinary("def main():\n" + body);

        Assert.Equal(2, e.Line);
        Assert.Equal(column, e.Column);
    }

    [Fact]
    public void TheOperatorsLengthIsTheUnderlineLength()
    {
        Assert.Equal(2, FirstBinary("def main():\n    b = a // 0\n").Length);
        Assert.Equal(1, FirstBinary("def main():\n    b = a + 1\n").Length);
        Assert.Equal(3, FirstBinary("def main():\n    b = a and 1\n").Length);
    }

    [Fact]
    public void EachComparisonInAChainIsLocatedAtItsOwnOperator()
    {
        // `a < b < c` becomes (a<b) and (b<c). Each comparison keeps its own operator; the
        // `and` joining them is synthesised, the user never wrote it, so it has NO position
        // rather than a borrowed one.
        //                                    1234567890123456789
        var chain = FirstBinary("def main():\n    r = a < b < c\n");

        Assert.Equal(BinaryOp.And, chain.Op);
        Assert.Equal(0, chain.Column);

        var left = Assert.IsType<BinaryExpr>(chain.Left);
        var right = Assert.IsType<BinaryExpr>(chain.Right);
        Assert.Equal(11, left.Column);   // the first '<'
        Assert.Equal(15, right.Column);  // the second '<'
    }

    [Fact]
    public void AnOperatorOnAContinuationLineTakesThatLinesPositionNotTheStatements()
    {
        // The line is stamped along with the column. An expression can span lines, and a
        // column from one line against a line number from another points at whatever happens
        // to sit there.
        var e = FirstBinary("def main():\n    b = (a\n         // 0)\n");

        Assert.Equal(3, e.Line);
        Assert.Equal(10, e.Column);
    }

    // ---- literals ------------------------------------------------------------------------

    [Theory]
    //           1234567890123
    [InlineData("    b = 7\n", 9, 1)]          // the `7`
    [InlineData("    b = 4096\n", 9, 4)]       // the whole number, underlined
    [InlineData("    b = 0xFF\n", 9, 4)]       // the prefix belongs to the token
    [InlineData("    b = 1.5\n", 9, 3)]        // a float is its token too
    [InlineData("    b = True\n", 9, 4)]
    [InlineData("    b = None\n", 9, 4)]
    public void ALiteralIsLocatedAtItsOwnToken(string body, int column, int length)
    {
        var prog = new Parser(new Lexer("def main():\n" + body).Tokenize()).ParseProgram();
        var stmt = Assert.IsType<Block>(prog.Functions[0].Body).Statements[0];
        Expression init = stmt switch
        {
            VarDecl v => v.Init!,
            AnnAssign a => a.Value!,
            AssignStmt sa => sa.Value,
            ExprStmt e => e.Expr,
            _ => throw new Xunit.Sdk.XunitException($"unexpected {stmt.GetType().Name}"),
        };

        Assert.Equal(2, init.Line);
        Assert.Equal(column, init.Column);
        Assert.Equal(length, init.Length);
    }

    [Fact]
    public void TheElementsOfABytesLiteralAreNotGivenAPositionOfTheirOwn()
    {
        // They are decoded from one b"..." token, so no element is anything the user typed.
        // The leaf convention is "a literal IS its token"; these have no token.
        var prog = new Parser(new Lexer("def main():\n    b = b\"\\x01\\x02\"\n").Tokenize())
            .ParseProgram();
        var stmt = Assert.IsType<Block>(prog.Functions[0].Body).Statements[0];
        Expression init = stmt switch
        {
            VarDecl v => v.Init!, AnnAssign a => a.Value!, AssignStmt sa => sa.Value,
            ExprStmt e => e.Expr,
            _ => throw new Xunit.Sdk.XunitException($"unexpected {stmt.GetType().Name}"),
        };
        var list = Assert.IsType<ListExpr>(init);

        Assert.All(list.Elements, el => Assert.Equal(0, el.Column));
    }
}
