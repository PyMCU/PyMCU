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
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU#177. A refused `yield` marks the KEYWORD, not the expression around it.
///
/// This is where the rule differs from `TupleExpr` and `ListExpr`, which are marked whole:
/// `yield from inner()` is eighteen characters and the call at the end of it is perfectly good
/// code. What the message blames is the construct, so that is what the caret covers.
///
/// The two spellings need two rules and only one of them has a number. A bare `yield` is one
/// token and takes its own length; `yield from` is measured across the two tokens, because
/// `yield  from` with extra spacing is valid Python and a written 10 would underline
/// `yield  fro`, one character inside the second keyword.
///
/// Both diagnostics come from one helper, `DelegateYieldPosition`, so widening it to carry the
/// node located both call sites at once. They are twins by SHARED CONSTRUCTOR rather than by
/// shared text: their messages differ, so a grep for the message finds neither pair.
/// </summary>
public class YieldPositionTests
{
    private static CompilerError Fails(string src) =>
        Assert.ThrowsAny<CompilerError>(() =>
            new IRGenerator().Generate(
                new Parser(new Lexer(src).Tokenize()).ParseProgram(),
                new Dictionary<string, ProgramNode>(),
                new DeviceConfig { Arch = "avr" }));

    private const string Head =
        "from pymcu.chips.atmega328p import GPIOR0, GPIOR1\n" +
        "from pymcu.types import uint8\n\n" +
        "def inner():\n    yield 1\n\n";

    [Theory]
    //                    1
    //          123456789012345678
    // line 8: "    x = yield from inner()"    the keyword pair starts at column 9
    // line 8: "    x = yield  from  inner()"  the same, two characters wider
    [InlineData("    x = yield from inner()\n", 10)]
    [InlineData("    x = yield  from  inner()\n", 11)]
    [InlineData("    x = yield\tfrom inner()\n", 10)]
    public void ADelegationMarksBothWordsAndMeasuresThem(string line, int length)
    {
        // The third row is a tab between the words, which is also valid and also not 10 by
        // arithmetic on spaces. Written lengths get all three of these wrong in two directions.
        var ex = Fails(Head + "def plain() -> uint8:\n" + line + "    return x\n" +
                       "def main() -> None:\n    GPIOR1.value = plain()\n");

        Assert.Contains("yield from", ex.Message);
        Assert.Equal(8, ex.Line);
        Assert.Equal(9, ex.Column);
        Assert.Equal(length, ex.Length);
    }

    [Fact]
    public void ABareYieldMarksItsOwnKeyword()
    {
        //                    1
        //          1234567890123456789
        // line 7: "    x = 1 + (yield)"   the `yield` is at column 14
        //
        // Five, and the five comes from the token rather than from a number written here. One
        // token has no interior space to stretch, which is why this spelling can carry a length
        // and the delegation cannot.
        var ex = Fails(
            "from pymcu.chips.atmega328p import GPIOR0, GPIOR1\n" +
            "from pymcu.types import uint8\n\n" +
            "def plain() -> uint8:\n" +
            "    x = 1 + (yield)\n" +
            "    return x\n" +
            "def main() -> None:\n    GPIOR1.value = plain()\n");

        Assert.Contains("yield", ex.Message);
        Assert.Equal(5, ex.Line);
        Assert.Equal(14, ex.Column);
        Assert.Equal(5, ex.Length);
    }

    [Fact]
    public void TheSecondCallerOfTheHelperIsLocatedToo()
    {
        // The same helper, reached from the generator loop rather than from the plain-function
        // loop. It is here because the two are twins by shared constructor: fixing the helper
        // fixed both, and a test on only one of them would pass while the other regressed.
        //
        //          123456789012
        // line 9: "    x = yield from inner()"  inside a generator this time
        var ex = Fails(Head +
                       "def gen():\n    yield 0\n    x = yield from inner()\n" +
                       "def main() -> None:\n    for v in gen():\n        GPIOR1.value = uint8(v)\n");

        Assert.Contains("yield from", ex.Message);
        Assert.Equal(9, ex.Line);
        Assert.Equal(9, ex.Column);
        Assert.Equal(10, ex.Length);
    }
}
