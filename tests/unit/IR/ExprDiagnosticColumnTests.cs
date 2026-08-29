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
/// PyMCU#177, the expression cluster. Each diagnostic points at the sub-expression it blames,
/// which for these is rarely the whole expression:
///
///   `a ** n`      the exponent, not the operator and not the base
///   `a in 5`      the container on the right, not the `in`
///   `d[9]`        the key, not the dict
///   `t.nope`      the member name
///   `s[0]`        the name that is a set, not the subscript
/// </summary>
public class ExprDiagnosticColumnTests
{
    private static CompilerError Fails(string body) =>
        Assert.Throws<CompilerError>(() =>
            new IRGenerator().Generate(
                new Parser(new Lexer("from pymcu.types import uint8\ndef main() -> None:\n" + body)
                    .Tokenize()).ParseProgram(),
                new Dictionary<string, ProgramNode>(),
                new DeviceConfig { Arch = "avr" }));

    [Theory]
    //           1234567890123456789012
    // line 3+ of "from pymcu...\ndef main...\n" + body
    [InlineData("    a: uint8 = 2\n    n: uint8 = 3\n    b: uint8 = a ** n\n", 5, 21)]  // the exponent
    [InlineData("    a: uint8 = 1\n    if a in 5:\n        pass\n", 4, 13)]              // the container
    [InlineData("    d = {1: 2}\n    v = d[9]\n", 4, 11)]                               // the key
    [InlineData("    xs: uint8[3] = [1, 2, 3]\n    v = xs[\"a\"]\n", 4, 12)]            // the index
    [InlineData("    s = {1, 2}\n    v = s[0]\n", 4, 9)]                                // the set's name
    [InlineData("    a: uint8 = 1\n    v = a.nope\n", 4, 11)]                           // the member
    public void ADiagnosticPointsAtTheSubExpressionItBlames(string body, int line, int column)
    {
        var ex = Fails(body);

        Assert.Equal(line, ex.Line);
        Assert.Equal(column, ex.Column);
    }

    [Fact]
    public void AnExponentThatIsAUnaryExpressionStillReportsNoColumn()
    {
        // `a ** -1` blames the exponent, which is a UnaryExpr. That node type is not stamped,
        // so there is no column and none is invented. Written to FLIP: when unary expressions
        // are stamped this fails, and the fix is to assert the real column, not to restore the
        // silence. Third guard of this shape in the issue; the previous two both collected.
        var ex = Fails("    a: uint8 = 2\n    b: uint8 = a ** -1\n");

        Assert.Contains("negative exponent", ex.Message);
        Assert.False(ex.HasColumn);
    }
}
