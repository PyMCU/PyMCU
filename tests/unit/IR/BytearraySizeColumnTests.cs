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
/// PyMCU#177. "bytearray: could not determine buffer size from initializer" is raised from TWO
/// sites with identical text, one for `xs = bytearray(...)` and one for the annotated
/// `xs: bytearray = bytearray(...)`. Both are covered here, because a fix applied to one of two
/// identical messages leaves the other reporting the old location and nothing points that out.
///
/// The caret marks the ARGUMENT the size was supposed to come from. Not the `bytearray(...)`
/// call, which is fine, and not the name being declared, which is also fine: the message says
/// the size could not be determined FROM THE INITIALIZER.
/// </summary>
public class BytearraySizeColumnTests
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
    //          12345678901234567890123456789012
    // line 5: "    xs = bytearray(0)"                 the `0` is at column 20
    // line 5: "    xs: bytearray = bytearray(0)"      the `0` is at column 31
    [InlineData("    xs = bytearray(0)\n", 20)]
    [InlineData("    xs: bytearray = bytearray(0)\n", 31)]
    public void AZeroSizedBytearrayPointsAtTheSize(string decl, int column)
    {
        var ex = Fails(
            "def main() -> None:\n" + decl +
            "    GPIOR1.value = uint8(len(xs))\n" +
            "    while True:\n        pass\n");

        Assert.Contains("could not determine buffer size", ex.Message);
        Assert.Equal(5, ex.Line);
        Assert.Equal(column, ex.Column);
        Assert.Equal(1, ex.Length);
    }

    [Theory]
    //                    1         2         3
    //          12345678901234567890123456789012
    // line 5: "    xs = bytearray([])"                the `[]` is at column 20
    // line 5: "    xs: bytearray = bytearray([])"     the `[]` is at column 31
    [InlineData("    xs = bytearray([])\n", 20)]
    [InlineData("    xs: bytearray = bytearray([])\n", 31)]
    public void AnEmptyListInitializerPointsAtTheList(string decl, int column)
    {
        // Was AnEmptyListInitializerCarriesTheRightNodeAndDrawsNoCaret, asserting the ceiling
        // with the instruction that stamping ListExpr would light it up with no edit here.
        // Lists are stamped now and it lit up with no edit here, so this is that instruction
        // being followed. The mark is the whole `[]`, both characters: what the message blames
        // is the list, and an empty list is nothing but its brackets.
        var ex = Fails(
            "def main() -> None:\n" + decl +
            "    GPIOR1.value = uint8(len(xs))\n" +
            "    while True:\n        pass\n");

        Assert.Contains("could not determine buffer size", ex.Message);
        Assert.Equal(5, ex.Line);
        Assert.Equal(column, ex.Column);
        Assert.Equal(2, ex.Length);
    }

    [Fact]
    public void AListWrittenAcrossLinesWithholdsTheUnderline()
    {
        // The span is the answer for a list and there is no span once it crosses lines. Both
        // front ends withhold, which is the same rule rather than two that happen to agree.
        var ex = Fails(
            "def main() -> None:\n" +
            "    xs = bytearray([\n" +
            "        ])\n" +
            "    GPIOR1.value = uint8(len(xs))\n" +
            "    while True:\n        pass\n");

        Assert.Contains("could not determine buffer size", ex.Message);
        Assert.Equal(5, ex.Line);
        Assert.Equal(20, ex.Column);
        Assert.Equal(1, ex.Length);   // the node carries Unlocated; UserError draws a bare caret
    }

    [Fact]
    public void ASizedBytearrayStillCompiles()
    {
        // The invariant. Every buffer in the stdlib goes through this path, and the two sites
        // above are on it.
        new IRGenerator().Generate(
            new Parser(new Lexer(
                "from pymcu.chips.atmega328p import GPIOR0, GPIOR1\n" +
                "from pymcu.types import uint8\n\n" +
                "def main() -> None:\n" +
                "    xs = bytearray(4)\n" +
                "    ys: bytearray = bytearray([1, 2, 3])\n" +
                "    GPIOR1.value = uint8(len(xs)) + uint8(len(ys))\n" +
                "    while True:\n        pass\n").Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });
    }
}
