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
using PyMCU.Common;
using PyMCU.Frontend;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU#219. A refusal points at the construct being refused.
///
/// `Parser.Error()` reports `Peek()`, which is correct for "Expected ')'" -- a message about
/// what is COMING, where Peek() is the thing that was not what it should have been. It is wrong
/// for "X is not supported", because X has been consumed by then.
///
/// Measured over all 17 refusals with written messages: 15 test with `Check()` before consuming,
/// so `Peek()` IS the construct and they were already right. Two had consumed it, and both
/// landed on the indentation rather than merely on a neighbouring token, because `Peek()` was
/// then a NEWLINE, whose column is a hardcoded 1. Those two are what this pins.
/// </summary>
public class RefusalPositionTests
{
    private static CompilerError Fails(string src) =>
        Assert.Throws<SyntaxError>(() =>
            new Parser(new Lexer(src).Tokenize()).ParseProgram());

    [Fact]
    public void NonlocalWithNoEnclosingFunctionPointsAtTheKeyword()
    {
        //          12345678901
        // line 2: "    nonlocal q"  -- `nonlocal` at column 5, eight characters
        var ex = Fails("def main():\n    nonlocal q\n");

        Assert.Contains("nonlocal", ex.Message);
        Assert.Equal(2, ex.Line);
        Assert.Equal(5, ex.Column);
        Assert.Equal(8, ex.Length);
    }

    [Fact]
    public void ClassmethodPointsAtTheDecoratorName()
    {
        //          123456
        // line 2: "    @classmethod"  -- the name at column 6, after the `@`
        var ex = Fails("class C:\n    @classmethod\n    def f(cls):\n        pass\n");

        Assert.Contains("@classmethod", ex.Message);
        Assert.Equal(2, ex.Line);
        Assert.Equal(6, ex.Column);
    }

    [Theory]
    // The fifteen that were already right, because they test before consuming. Pinned so a
    // later conversion to ErrorAt does not move them off a position that is already correct.
    //                                                     line col
    [InlineData("def main():\n    b = a @ 2\n", 2, 11)]              // the `@`
    [InlineData("def main():\n    x = 1\n    del x\n", 3, 5)]        // the `del`
    [InlineData("def main():\n    b = +a\n", 2, 9)]                  // the unary `+`
    public void ARefusalThatTestsBeforeConsumingAlreadyPointsAtItsConstruct(
        string src, int line, int column)
    {
        var ex = Fails(src);

        Assert.Equal(line, ex.Line);
        Assert.Equal(column, ex.Column);
    }
}
