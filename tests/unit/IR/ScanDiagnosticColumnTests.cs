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
/// PyMCU#177, the Scan.cs tail. These five ran during the SCAN, before anything had been
/// lowered, and that is what made them the worst of the issue: with no column, UserError falls
/// back to lastLine, and during the scan lastLine is still 0, so the fallback fell through to
/// its own last resort and reported LINE 1. Five diagnostics pointing at the import at the top
/// of the file, on programs whose actual mistake was thirty lines down.
///
/// A missing caret is visible as a missing caret. "Line 1" is a claim, and it is wrong in a way
/// that reads as information.
/// </summary>
public class ScanDiagnosticColumnTests
{
    private static CompilerError Fails(string src) =>
        Assert.ThrowsAny<CompilerError>(() =>
            new IRGenerator().Generate(
                new Parser(new Lexer("from pymcu.types import uint8\n" + src).Tokenize())
                    .ParseProgram(),
                new Dictionary<string, ProgramNode>(),
                new DeviceConfig { Arch = "avr" }));

    private static void At(CompilerError ex, int line, int column, int length)
    {
        Assert.Equal(line, ex.Line);
        Assert.Equal(column, ex.Column);
        Assert.Equal(length, ex.Length);
    }

    [Fact]
    public void APioAssemblyErrorPointsAtTheProgramsDef()
    {
        //          1234
        // line 3: "def prog():"
        //
        // The `def`, not the offending instruction. The assembler's own message already names
        // the instruction; what this frame adds is WHICH program, so the caret marks the
        // program. Reached through a bare @asm_pio, which needs no rp2 import.
        var ex = Fails("@asm_pio()\ndef prog():\n    nosuchinstr()\ndef main() -> None:\n    pass\n");

        Assert.Contains("in PIO program 'prog'", ex.Message);
        At(ex, 3, 1, 3);
    }

    [Theory]
    //          1234567890
    // line 2: "def take(buf: uint8[4]) -> uint8:"  -- the parameter starts at column 10
    [InlineData("def take(buf: uint8[4]) -> uint8:\n    return buf[0]\n", 3, "fixed-array type")]
    [InlineData("def take(items: list[uint8]) -> uint8:\n    return 1\n", 5, "list parameters")]
    public void AParameterDiagnosticPointsAtTheParameter(string decl, int length, string fragment)
    {
        // The parameter, not the enclosing `def` and not the annotation. The message names the
        // parameter, and the annotation is what the reader is being told to CHANGE -- a caret
        // under `uint8[4]` reads as "this type does not exist", which is the opposite.
        var ex = Fails(decl + "def main() -> None:\n    pass\n");

        Assert.Contains(fragment, ex.Message);
        At(ex, 2, 10, length);
    }

    [Fact]
    public void MultipleInheritancePointsAtTheClassKeyword()
    {
        //           123456
        // line 10: "class C(A, B):"
        //
        // The `class`, for the same reason a missing return marks the `def`: what is refused is
        // the declaration as a whole. The bases are names in a list, not nodes, so there is no
        // second base to point at even if that read better.
        var ex = Fails(
            "class A:\n    def __init__(self) -> None:\n        self.a: uint8 = 1\n" +
            "class B:\n    def __init__(self) -> None:\n        self.b: uint8 = 2\n" +
            "class C(A, B):\n    def __init__(self) -> None:\n        self.c: uint8 = 3\n" +
            "def main() -> None:\n    c = C()\n");

        Assert.Contains("multiple inheritance", ex.Message);
        At(ex, 8, 1, 5);
    }

    [Fact]
    public void ADuplicateMethodPointsAtTheSecondDefinition()
    {
        //          12345678
        // line 7: "    def get(self) -> uint8:"  -- the SECOND one
        //
        // The second definition and not the first: the first is a perfectly good method until
        // the second arrives, so the second is the line to delete or rename.
        var ex = Fails(
            "class Box:\n    def __init__(self, n: uint8) -> None:\n        self.n: uint8 = n\n" +
            "    def get(self) -> uint8:\n        return self.n\n" +
            "    def get(self) -> uint8:\n        return self.n\n" +
            "def main() -> None:\n    b = Box(1)\n    v: uint8 = b.get()\n");

        Assert.Contains("more than once", ex.Message);
        At(ex, 7, 5, 3);
    }
}
