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

    [Fact]
    public void ASliceOfSomethingThatIsNotAnArrayPointsAtTheName()
    {
        //                   1         2
        //          1234567890123456789012
        // line 6: "    xs: uint8[2] = ys[0:2]"  -- `ys` is at column 20
        //
        // The name and not the slice. The message says the target must be a NAMED FIXED-SIZE
        // ARRAY, and `ys` is the name that is not one; the brackets are written correctly.
        //
        // #177 listed this site as unmeasured, with "a slice of a call result, of a bytes
        // literal, or of a module-level list" as the things to try. All three are ruled out by
        // the guarding condition, which requires `idxRhs.Target is VariableExpr`: a call
        // result, a bytes literal and a list literal are not names, so they never enter the
        // branch at all. What reaches it is any NAME that is not a known fixed-size array, and
        // seven programs do: a scalar local, a str local, a scalar parameter, a str parameter,
        // an undeclared name, a module-level plain list and a module-level str.
        //
        // A scalar local is the shortest of the seven and is the one pinned here.
        var ex = Fails(
            "def main() -> None:\n" +
            "    ys: uint8 = 5\n" +
            "    xs: uint8[2] = ys[0:2]\n" +
            "    GPIOR1.value = xs[0]\n" +
            "    while True:\n        pass\n");

        Assert.Contains("Slice initializer target must be a named fixed-size array", ex.Message);
        Assert.Equal(6, ex.Line);
        Assert.Equal(20, ex.Column);
        Assert.Equal(2, ex.Length);
    }

}
