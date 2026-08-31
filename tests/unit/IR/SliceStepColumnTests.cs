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
/// PyMCU#177, one of the thirty: the slice step.
///
/// The probe took two attempts, and the first reached a DIFFERENT diagnostic that says
/// something similar. `xs[0:3:0] = [...]` looks like the way to reach "Slice step cannot be
/// zero" and is refused earlier by the slice-ASSIGNMENT guard; the step site lives on the
/// initializer path, `xs: uint8[4] = ys[0:4:0]`. Reading the message rather than the exit code
/// is what separates them, and it is the only thing that does.
/// </summary>
public class SliceStepColumnTests
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
    public void ASliceStepOfZeroPointsAtTheStep()
    {
        //                   1         2
        //          123456789012345678901234567
        // line 6: "    xs: uint8[4] = ys[0:4:0]"  -- the step is at column 27
        //
        // The step and not the slice: the message is about that one number, and the two other
        // numbers in the same brackets are fine. An absent step defaults to 1, so the node is
        // never null where this fires.
        var ex = Fails(
            "def main() -> None:\n" +
            "    ys: uint8[4] = [1, 2, 3, 4]\n" +
            "    xs: uint8[4] = ys[0:4:0]\n" +
            "    GPIOR1.value = xs[0]\n" +
            "    while True:\n        pass\n");

        Assert.Contains("Slice step cannot be zero", ex.Message);
        Assert.Equal(6, ex.Line);
        Assert.Equal(27, ex.Column);
        Assert.Equal(1, ex.Length);
    }

}
