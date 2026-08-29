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
/// A coroutine is located at `async def`, BOTH words, and a plain function at its `def`.
///
/// What a stamp marks is the INTRODUCER of the construct. For a coroutine the introducer is
/// two words, and marking only the second started the underline in the middle of a compound
/// keyword. It also disagreed with the other front end: CPython puts an AsyncFunctionDef at
/// its `async`, so the two named different characters for the same function.
///
/// These read the AST rather than a diagnostic on purpose. A stamp checked only through
/// whichever error happens to consume it is pinned by that error's choices too, and there is
/// no reason the position of a node should depend on which message reads it.
/// </summary>
public class AsyncDefPositionTests
{
    private static FunctionDef First(string src) =>
        new Parser(new Lexer(src).Tokenize()).ParseProgram().Functions[0];

    [Theory]
    //            1234567890123456
    [InlineData("async def f() -> None:\n    pass\n", 1, 1, 9)]
    // Valid Python, and the case a constant 9 gets wrong: it would mark `async   d`, one
    // character into `def` and stopping short of it. The length is measured from the two
    // tokens, so it follows whatever gap the author wrote.
    [InlineData("async   def f() -> None:\n    pass\n", 1, 1, 11)]
    [InlineData("async\tdef f() -> None:\n    pass\n", 1, 1, 9)]
    public void ACoroutineIsLocatedAtBothWordsOfItsIntroducer(
        string src, int line, int column, int length)
    {
        var fn = First(src);

        Assert.True(fn.IsAsync);
        Assert.Equal(line, fn.Line);
        Assert.Equal(column, fn.Column);
        Assert.Equal(length, fn.Length);
    }

    [Fact]
    public void APlainFunctionStillMarksItsDefAlone()
    {
        // The invariant beside the change. `async` is a soft keyword handled on its own path,
        // so a stamp applied one branch too wide would take every ordinary function with it
        // and nothing in the async tests would notice.
        var fn = First("def f() -> None:\n    pass\n");

        Assert.False(fn.IsAsync);
        Assert.Equal(1, fn.Line);
        Assert.Equal(1, fn.Column);
        Assert.Equal(3, fn.Length);
    }

    [Fact]
    public void ACoroutineInsideAClassIsLocatedAtItsOwnIntroducer()
    {
        //              12345678901234
        // line 2: "    async def get(self) -> None:"  -- `async` starts at column 5
        //
        // A method is refused later, for being a coroutine in a class, but the refusal reads
        // this stamp and the two arrive by different paths: a method is parsed through the
        // statement dispatch and a module-level coroutine through the program loop. Both were
        // stamped, so both are checked.
        var prog = new Parser(new Lexer(
            "class Box:\n    async def get(self) -> None:\n        pass\n").Tokenize())
            .ParseProgram();
        var cls = Assert.IsType<ClassDef>(Assert.Single(prog.GlobalStatements));
        var body = Assert.IsType<Block>(cls.Body);
        var method = Assert.IsType<FunctionDef>(body.Statements[0]);

        Assert.True(method.IsAsync);
        Assert.Equal(2, method.Line);
        Assert.Equal(5, method.Column);
        Assert.Equal(9, method.Length);
    }
}
