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
/// PyMCU#224. `for i in (range(n))` means `for i in range(n)`; the parentheses group and
/// CPython does not record them at all.
///
/// The bounded-loop form is chosen by peeking for a bare `range` after `in`, so one parenthesis
/// sent the loop down the general-iterable path, whose own range handler only accepts a
/// constant bound. `(range(3))` therefore compiled and `(range(n))` did not, a difference the
/// source does not have.
///
/// The parentheses are counted, not peeled blindly: after `in` a parenthesis is not always
/// grouping. `for v in (1, 2)` is a TUPLE and keeps its parentheses, which is what the second
/// half of these tests holds down.
/// </summary>
public class ForRangeParenthesesTests
{
    private static ForStmt FirstFor(string body)
    {
        var prog = new Parser(new Lexer("def main():\n" + body).Tokenize()).ParseProgram();
        var stmt = Assert.IsType<Block>(prog.Functions[0].Body).Statements[0];
        // A `for` may be wrapped by the loop-else desugaring; take the first ForStmt either way.
        if (stmt is ForStmt direct) return direct;
        var block = Assert.IsType<Block>(stmt);
        return Assert.IsType<ForStmt>(block.Statements.Find(s => s is ForStmt));
    }

    [Theory]
    [InlineData("    for i in range(n):\n        pass\n")]
    [InlineData("    for i in (range(n)):\n        pass\n")]
    [InlineData("    for i in ((range(n))):\n        pass\n")]
    [InlineData("    for i in ( range(n) ):\n        pass\n")]
    public void AParenthesisedRangeIsTheBoundedLoopForm(string body)
    {
        var f = FirstFor(body);

        Assert.NotNull(f.RangeStop);          // the bounded form, whatever the spelling
        Assert.Null(f.Iterable);              // NOT the general-iterable path
        Assert.Equal("n", Assert.IsType<VariableExpr>(f.RangeStop).Name);
    }

    [Theory]
    [InlineData("    for i in (range(1, n)):\n        pass\n")]
    [InlineData("    for i in (range(0, n, 2)):\n        pass\n")]
    public void TheOtherRangeAritiesGroupToo(string body)
    {
        var f = FirstFor(body);

        Assert.NotNull(f.RangeStart);
        Assert.NotNull(f.RangeStop);
        Assert.Null(f.Iterable);
    }

    [Theory]
    // A parenthesis after `in` that is NOT grouping. Each must stay on the iterable path:
    // peeling here would turn a tuple into its first element, silently.
    [InlineData("    for v in (1, 2):\n        pass\n")]
    [InlineData("    for v in (1,):\n        pass\n")]
    [InlineData("    for v in (range(2), range(2)):\n        pass\n")]
    public void AParenthesisThatBuildsATupleIsLeftAlone(string body)
    {
        var f = FirstFor(body);

        Assert.Null(f.RangeStop);
        Assert.IsType<TupleExpr>(f.Iterable);
    }

    [Fact]
    public void AParenthesisedRangeInALargerExpressionIsNotTheBoundedForm()
    {
        // `(range(n))` grouping is only grouping when the parentheses close immediately and the
        // header ends. Anything else -- here a subscript on the parenthesised call -- has to go
        // down the general path, or the lookahead would swallow syntax it does not own.
        var f = FirstFor("    for v in (range(n))[0]:\n        pass\n");

        Assert.Null(f.RangeStop);
        Assert.NotNull(f.Iterable);
    }

    // ---- PyMCU#228: the trailing comma, which is legal in every Python call --------------

    [Theory]
    [InlineData("    for i in range(n,):\n        pass\n", 1)]
    [InlineData("    for i in range(1, n,):\n        pass\n", 2)]
    [InlineData("    for i in range(0, n, 2,):\n        pass\n", 3)]
    [InlineData("    for i in (range(0, n, 2,)):\n        pass\n", 3)]
    public void ATrailingCommaEndsTheArgumentListLikeAnyOtherCall(string body, int args)
    {
        // `len(xs,)` always compiled, because ParsePostfix checks for `)` after a comma. This
        // header parses its own argument list and did not, so the same comma was accepted in
        // one call and refused in another, in the same program.
        var f = FirstFor(body);

        Assert.NotNull(f.RangeStop);
        if (args >= 2) Assert.NotNull(f.RangeStart);
        if (args >= 3) Assert.NotNull(f.RangeStep);
    }

    [Theory]
    // A comma with nothing in front of it, and a doubled comma, stay errors. The trailing-comma
    // rule ends a list that has already started; it does not make commas optional.
    [InlineData("    for i in range(,):\n        pass\n")]
    [InlineData("    for i in range(n,,):\n        pass\n")]
    [InlineData("    for i in range():\n        pass\n")]
    public void ACommaWithNoArgumentIsStillAnError(string body)
    {
        Assert.Throws<PyMCU.Common.SyntaxError>(
            () => new Parser(new Lexer("def main():\n" + body).Tokenize()).ParseProgram());
    }
}
