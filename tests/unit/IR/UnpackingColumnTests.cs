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

    [Theory]
    // Was TooFewValuesCarriesTheRightNodeAndStillDrawsNoCaret, which asserted that the node was
    // passed and the caret still withheld because the parser did not stamp a TupleExpr. Tuples
    // are stamped now, so this is that instruction being followed.
    //
    //                    1
    //          1234567890123456789
    // line 5: "    a, b, *c = (1,)"   the tuple is `(1,)`, four characters from column 16
    // line 5: "    a, b, *c = 1,"     written bare, it is `1,`, two characters from column 16
    //
    // Both spellings, because the tuple's START differs between them and its LENGTH differs
    // too: the parenthesised one begins at the `(` and the bare one at its first element. One
    // row would have pinned whichever of the two happened to be written here.
    [InlineData("    a, b, *c = (1,)\n", 4)]
    [InlineData("    a, b, *c = 1,\n", 2)]
    public void TooFewValuesPointsAtTheTupleThatIsShort(string line, int length)
    {
        var ex = Fails(
            "def main() -> None:\n" + line +
            "    GPIOR1.value = uint8(a)\n" +
            "    while True:\n        pass\n");

        Assert.Contains("Not enough values to unpack", ex.Message);
        Assert.Equal(5, ex.Line);
        Assert.Equal(16, ex.Column);
        Assert.Equal(length, ex.Length);
    }

    [Fact]
    public void ATupleWrittenAcrossLinesWithholdsTheUnderline()
    {
        // The span is the answer for a tuple and there is no span once the node crosses lines:
        // an underline measured on one line and drawn on another marks whatever sits under it.
        // Both front ends refuse here rather than one of them guessing, which is what makes the
        // rule one rule instead of two.
        var ex = Fails(
            "def main() -> None:\n" +
            "    a, b, *c = (1,\n" +
            "                )\n" +
            "    GPIOR1.value = uint8(a)\n" +
            "    while True:\n        pass\n");

        Assert.Contains("Not enough values to unpack", ex.Message);
        Assert.Equal(5, ex.Line);
        Assert.Equal(16, ex.Column);
        // 1 and not 0: the NODE carries CompilerError.Unlocated, and UserError renders an
        // unknown length as a bare caret rather than as no mark at all. What is pinned is that
        // the underline does not grow to a span, which is the thing that would be wrong.
        Assert.Equal(1, ex.Length);
    }
}
