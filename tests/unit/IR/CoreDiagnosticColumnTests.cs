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
/// PyMCU#177, the Core.cs tail. Both of these are built by a HELPER that was given only a
/// name, so there was no node to pass however much the caller had one -- and both callers had
/// one all along.
///
/// The choice of WHICH node is the whole content of the change, and in both cases the obvious
/// candidate is the wrong one. `xs[i]` has three: the subscript, the index and the array, and
/// the message asks the reader to declare a type for the array. A caret under `i` would send
/// them to make the index constant, which is precisely the reading the message was written to
/// prevent.
/// </summary>
public class CoreDiagnosticColumnTests
{
    private static CompilerError Fails(string body) =>
        Assert.ThrowsAny<CompilerError>(() =>
            new IRGenerator().Generate(
                new Parser(new Lexer(
                    "from pymcu.chips.atmega328p import GPIOR0, GPIOR1\n" +
                    "from pymcu.types import uint8\n\n" + body).Tokenize()).ParseProgram(),
                new Dictionary<string, ProgramNode>(),
                new DeviceConfig { Arch = "avr" }));

    [Fact]
    public void AnUnrolledArrayIndexPointsAtTheArray()
    {
        //                   1         2
        //          1234567890123456789012345678
        // line 7: "    GPIOR1.value = uint8(xs[i])"  -- `xs` starts at column 26
        var ex = Fails(
            "def main() -> None:\n" +
            "    xs = [1, 2, 3]\n" +
            "    i: uint8 = GPIOR0.value\n" +
            "    GPIOR1.value = uint8(xs[i])\n" +
            "    while True:\n        pass\n");

        // Asserts that the message NAMES THE ARRAY, not the clause it uses to do so. The old
        // wording ("has no declared array type") was replaced in #246 because it is false for
        // the other way into this site -- an array that IS declared and loses its addressability
        // when passed to an @inline. This test is about the caret, and pinning the prose made a
        // wording fix look like a regression.
        Assert.Contains("'xs'", ex.Message);
        Assert.Equal(7, ex.Line);
        Assert.Equal(26, ex.Column);
        Assert.Equal(2, ex.Length);
    }

    [Fact]
    public void AMultiValuedStringPointsAtTheReadThatCannotBeLowered()
    {
        //                   1         2
        //          1234567890123456789012
        // line 8: "    n: uint8 = len(s)"  -- the `s` being read is at column 20
        //
        // The READ, not the assignments that gave the name its several texts. Those are
        // elsewhere, they are both legal, and the message already names them by their texts;
        // the read is the one line the reader has to change.
        var ex = Fails(
            "def main() -> None:\n" +
            "    s = \"aa\"\n" +
            "    if GPIOR0.value > 1:\n" +
            "        s = \"bb\"\n" +
            "    n: uint8 = len(s)\n" +
            "    GPIOR1.value = n\n" +
            "    while True:\n        pass\n");

        Assert.Contains("no single compile-time value", ex.Message);
        Assert.Equal(8, ex.Line);
        Assert.Equal(20, ex.Column);
        Assert.Equal(1, ex.Length);
    }
}
