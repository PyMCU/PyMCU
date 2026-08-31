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
/// PyMCU#177, the unpacking diagnostics. All three say what the RIGHT-HAND SIDE has to be, so
/// the caret marks the right-hand side.
///
/// The target list is the tempting alternative and it is wrong in every one of them: `a, b = xs`
/// is a perfectly good target list, and a caret under it would send the reader to rewrite the
/// half that is correct. Same shape as the parameter diagnostics, where the caret belongs on
/// the parameter and not on the enclosing `def`.
/// </summary>
public class UnpackingColumnTests
{
    private static CompilerError Fails(string body) =>
        Assert.ThrowsAny<CompilerError>(() =>
            new IRGenerator().Generate(
                new Parser(new Lexer(
                    "from pymcu.chips.atmega328p import GPIOR0, GPIOR1\n" +
                    "from pymcu.types import uint8\n\n" + body).Tokenize()).ParseProgram(),
                new Dictionary<string, ProgramNode>(),
                new DeviceConfig { Arch = "avr" }));

    [Theory]
    //                    1
    //          1234567890123
    // line 6: "    a, *b = xs"   the source is at column 13
    // line 6: "    a, b = xs"    the source is at column 12
    [InlineData("    a, *b = xs\n", 13, "starred unpacking target")]
    [InlineData("    a, b = xs\n", 12, "must be a tuple literal")]
    public void UnpackingAnArrayByNamePointsAtTheArray(string line, int column, string fragment)
    {
        var ex = Fails(
            "def main() -> None:\n" +
            "    xs: uint8[3] = [1, 2, 3]\n" + line +
            "    GPIOR1.value = uint8(a)\n" +
            "    while True:\n        pass\n");

        Assert.Contains(fragment, ex.Message);
        Assert.Equal(6, ex.Line);
        Assert.Equal(column, ex.Column);
        Assert.Equal(2, ex.Length);
    }

    [Fact]
    public void TooFewValuesCarriesTheRightNodeAndStillDrawsNoCaret()
    {
        // The node passed is the one to blame and the site is finished; what is missing is one
        // level down. The right-hand side here is a TUPLE LITERAL, and the parser does not
        // stamp a TupleExpr, so the correct node arrives without a position.
        //
        // Written to FLIP: when tuples are stamped this fails, and the fix is to assert the
        // real column of `(1,)` rather than to stop passing the node. The line is asserted
        // because that is what this site does get right, and a guard that checks only the
        // absence of a column would not notice the line drifting.
        var ex = Fails(
            "def main() -> None:\n" +
            "    a, b, *c = (1,)\n" +
            "    GPIOR1.value = uint8(a)\n" +
            "    while True:\n        pass\n");

        Assert.Contains("Not enough values to unpack", ex.Message);
        Assert.Equal(5, ex.Line);
        Assert.False(ex.HasColumn);
    }
}
