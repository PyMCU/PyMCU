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
/// A diagnostic that names a NAME should point at that name, not at the start of the
/// statement that contains it. `c = a + b + undefined_name + a` reported column 1 for every
/// one of these, because the only position IR generation carried was the statement's line.
///
/// The column travels from the lexer, which has recorded one on every token since the
/// beginning, through the token that built the expression node, to the node itself. These
/// tests pin the arrival, not the mechanism.
/// </summary>
public class DiagnosticColumnTests
{
    private static ProgramNode Ast(string src) =>
        new Parser(new Lexer(src).Tokenize()).ParseProgram();

    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(Ast(src), new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });

    private static CompilerError Fails(string src) =>
        Assert.Throws<CompilerError>(() => Gen(src));

    // ---- the reported case ---------------------------------------------------------------

    [Fact]
    public void AnUndefinedName_PointsAtTheNameAndNotAtColumnOne()
    {
        //           1234567890123456789012345678
        // line 4:  "    c: uint8 = a + b + undefined_name + a"
        //                                  ^ column 24
        var ex = Fails(
            "from pymcu.types import uint8\n" +
            "def main():\n" +
            "    a: uint8 = 1\n" +
            "    c: uint8 = a + undefined_name\n");

        Assert.Contains("undefined_name", ex.Message);
        Assert.Equal(4, ex.Line);
        Assert.Equal(20, ex.Column);
    }

    [Fact]
    public void AnUndefinedName_UnderlinesTheWholeName()
    {
        var ex = Fails(
            "from pymcu.types import uint8\n" +
            "def main():\n" +
            "    a: uint8 = 1\n" +
            "    c: uint8 = a + undefined_name\n");

        Assert.Equal("undefined_name".Length, ex.Length);
    }

    [Fact]
    public void TwoUndefinedNamesOnOneLine_AreDistinguishedByTheirColumn()
    {
        // The whole point of a column: same line, same message shape, different place.
        var first = Fails(
            "from pymcu.types import uint8\n" +
            "def main():\n" +
            "    c: uint8 = alpha\n");
        var second = Fails(
            "from pymcu.types import uint8\n" +
            "def main():\n" +
            "    c: uint8 = 1 + beta\n");

        Assert.Equal(16, first.Column);
        Assert.Equal(20, second.Column);
    }

    // ---- the callee cluster --------------------------------------------------------------

    [Fact]
    public void ACallToAnUndefinedFunction_PointsAtTheCalleeName()
    {
        //           1234567890123456789
        // line 3:  "    no_such_func(1)"
        //               ^ column 5
        var ex = Fails(
            "from pymcu.types import uint8\n" +
            "def main():\n" +
            "    no_such_func(1)\n");

        Assert.Contains("no_such_func", ex.Message);
        Assert.Equal(3, ex.Line);
        Assert.Equal(5, ex.Column);
    }

    [Fact]
    public void AnUnsupportedBuiltin_PointsAtTheBuiltinName()
    {
        var ex = Fails(
            "from pymcu.types import uint8\n" +
            "def main():\n" +
            "    x: uint8 = 1\n" +
            "    y = getattr(x, 'a')\n");

        Assert.Contains("reflection", ex.Message);
        Assert.Equal(4, ex.Line);
        Assert.Equal(9, ex.Column);
    }

    // ---- the rule: never invent one ------------------------------------------------------

    [Fact]
    public void AWholeFunctionDiagnosticPointsAtTheDefThatDeclaredIt()
    {
        // This was the FIRST guard written for this issue, and it asserted the opposite: a
        // missing return is a property of the whole function, so column 0 and no caret.
        //
        // That reasoning held only while nothing could name a function. It does now: the `def`
        // keyword is the whole construct's token, so the diagnostic marks the function whose
        // declared contract is unmet. "No single character to blame" turned out to mean "no
        // character INSIDE the body", which is a different claim from having no position.
        //
        // Fourth guard in this issue to convert, and the one that took longest, because the
        // node type it was waiting on was the last of six to be stamped.
        var ex = Fails(
            "from pymcu.types import uint8\n" +
            "def f() -> uint8:\n" +
            "    x: uint8 = 1\n" +
            "def main():\n" +
            "    y: uint8 = f()\n");

        Assert.Equal(2, ex.Line);
        Assert.Equal(1, ex.Column);
        Assert.Equal(3, ex.Length);
    }
}
