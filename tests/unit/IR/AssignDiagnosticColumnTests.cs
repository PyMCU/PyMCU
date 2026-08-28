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
/// PyMCU#177, the assignment cluster. A diagnostic about an assignment points at the thing it
/// blames: the target name, the target expression, or the value.
///
/// Thirty-four sites now carry a node. Only some of them draw a caret today, and the split is
/// entirely about whether the blamed node's TYPE is stamped: a `VariableExpr` target is, so it
/// points; a `SliceExpr`, `IndexExpr`, `MemberAccessExpr` or `ListCompExpr` is not, so it
/// correctly reports nothing. Those become carets when their node type is stamped, with no
/// edit here, which is the same shape as the literal case: pointing is necessary, stamping is
/// what makes it visible.
/// </summary>
public class AssignDiagnosticColumnTests
{
    private static CompilerError Fails(string src) =>
        Assert.Throws<CompilerError>(() =>
            new IRGenerator().Generate(
                new Parser(new Lexer(src).Tokenize()).ParseProgram(),
                new Dictionary<string, ProgramNode>(),
                new DeviceConfig { Arch = "avr" }));

    [Fact]
    public void AssigningIntoACompileTimeDictPointsAtTheNameItBlames()
    {
        //          12345
        // line 4: "    d[5] = 6"  -- `d` is at column 5
        var ex = Fails(
            "from pymcu.types import uint8\n" +
            "def main() -> None:\n" +
            "    d = {1: 2, 3: 4}\n" +
            "    d[5] = 6\n");

        Assert.Contains("compile-time dict literal", ex.Message);
        Assert.Equal(4, ex.Line);
        Assert.Equal(5, ex.Column);
    }

    [Fact]
    public void AnInvalidAssignmentTargetPointsAtTheTargetExpression()
    {
        //          1234567
        // line 4: "    a + 1 = 2"  -- the target is `a + 1`, a BinaryExpr, at its operator
        var ex = Fails(
            "from pymcu.types import uint8\n" +
            "def main() -> None:\n" +
            "    a: uint8 = 1\n" +
            "    a + 1 = 2\n");

        Assert.Contains("Invalid assignment target", ex.Message);
        Assert.Equal(4, ex.Line);
        Assert.Equal(7, ex.Column);
    }

    [Fact]
    public void ASliceDiagnosticPointsAtTheSlicesColon()
    {
        //          123456789
        // line 4: "    xs[0:2] = [9]"  -- the `:` is at column 9
        //
        // Written asserting the opposite, that the site carried its SliceExpr but drew no
        // caret because SliceExpr was unstamped, with the instruction that if it started
        // failing the fix was to assert the real column rather than restore the silence.
        // Slices were stamped in the next change, it failed, and this is that instruction
        // being followed. Second test in this issue to convert itself on schedule.
        var ex = Fails(
            "from pymcu.types import uint8\n" +
            "def main() -> None:\n" +
            "    xs: uint8[3] = [1, 2, 3]\n" +
            "    xs[0:2] = [9]\n");

        Assert.Contains("slice assignment", ex.Message);
        Assert.Equal(4, ex.Line);
        Assert.Equal(9, ex.Column);
    }

    [Fact]
    public void AnIndexDiagnosticCarriesItsNodeButDrawsNoCaretYet()
    {
        // The guard the slice test used to provide, moved to a type that still lacks a stamp.
        // IndexExpr is passed by the site that blames it and has no position of its own, so no
        // caret is drawn. Same instruction: when this fails, assert the column.
        var ex = Fails(
            "from pymcu.types import uint8\n" +
            "def main() -> None:\n" +
            "    xs: uint8[4] = [1, 2, 3, 4]\n" +
            "    xs[0:4:0] = [1, 2, 3, 4]\n");

        Assert.Contains("slice assignment", ex.Message);
        Assert.False(ex.HasColumn);
    }
}
