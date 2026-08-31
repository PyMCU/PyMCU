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
/// PyMCU#177. The two `input()` refusals sit seven lines apart in one `if`, and they blame
/// different halves of the same call:
///
///   a keyword input() HAS, carrying something that is not a literal   the VALUE is wrong
///   an argument that is none of the accepted shapes                   that ARGUMENT is wrong
///
/// Neither marks the call. A call can pass several arguments with only one of them wrong, and a
/// caret on `input` leaves the reader to work out which. The third refusal in the same block,
/// for a keyword `input()` does not have, DOES mark the call, and that is the right split: there
/// the key itself is the mistake and the value is beside the point.
/// </summary>
public class InputArgumentColumnTests
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
    //                    1         2         3
    //          123456789012345678901234567890123456
    // line 5: "    xs: bytearray = input(prompt=1)"        the `1` is at column 34
    // line 5: "    xs: bytearray = input(maxlen=\"8\")"   the `"8"` is at column 34
    [InlineData("    xs: bytearray = input(prompt=1)\n", 1, "'prompt'")]
    [InlineData("    xs: bytearray = input(maxlen=\"8\")\n", 3, "'maxlen'")]
    public void AKeywordCarryingTheWrongLiteralPointsAtTheValue(string decl, int length, string key)
    {
        // The value and not the key: the key is spelled correctly and is one input() accepts.
        // Both spellings start at the same column and differ in width, which is why one row
        // would not have been enough.
        var ex = Fails(
            "def main() -> None:\n" + decl +
            "    GPIOR1.value = uint8(len(xs))\n" +
            "    while True:\n        pass\n");

        Assert.Contains(key, ex.Message);
        Assert.Contains("must be a compile-time", ex.Message);
        Assert.Equal(5, ex.Line);
        Assert.Equal(34, ex.Column);
        Assert.Equal(length, ex.Length);
    }

    [Fact]
    public void AnArgumentOfNoAcceptedShapePointsAtThatArgument()
    {
        //                    1         2
        //          1234567890123456789012345678
        // line 6: "    xs: bytearray = input(n)"   the `n` is at column 27
        var ex = Fails(
            "def main() -> None:\n" +
            "    n: uint8 = GPIOR0.value\n" +
            "    xs: bytearray = input(n)\n" +
            "    GPIOR1.value = uint8(len(xs))\n" +
            "    while True:\n        pass\n");

        Assert.Contains("input(): arguments must be", ex.Message);
        Assert.Equal(6, ex.Line);
        Assert.Equal(27, ex.Column);
        Assert.Equal(1, ex.Length);
    }

    [Theory]
    [InlineData("    xs: bytearray = input(\"name? \", 8)\n")]
    [InlineData("    xs: bytearray = input(prompt=\"age? \", maxlen=4)\n")]
    public void AnAcceptedSpellingGetsPastTheArgumentLoop(string decl)
    {
        // The invariant, and it asserts something weaker than "compiles" on purpose. `input()`
        // lowers to a UART write, and this harness has no chip HAL, so an accepted spelling
        // still fails here with "call to undefined function 'uart_write_str'". Writing this as
        // a plain compile check produced a red test for a reason that has nothing to do with
        // the change.
        //
        // What is pinned is what the change could break: both accepted spellings pass THROUGH
        // the argument loop that raises the two refusals above, and neither refusal fires. A
        // guard placed one condition too wide would catch them, and this says so.
        var ex = Fails(
            "def main() -> None:\n" + decl +
            "    GPIOR1.value = uint8(len(xs))\n" +
            "    while True:\n        pass\n");

        Assert.DoesNotContain("must be a compile-time", ex.Message);
        Assert.DoesNotContain("input(): arguments must be", ex.Message);
    }
}
