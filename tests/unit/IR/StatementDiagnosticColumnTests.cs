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
/// PyMCU#177, the statement and control-flow tail. Nineteen sites now carry the node they
/// blame. Only some draw a caret, and the split is the familiar one: an EXPRESSION node is
/// stamped and points, a STATEMENT node is not and correctly reports nothing.
///
/// Every node passed here is a SYNTACTIC CHILD of the statement being lowered. That is the
/// condition, not a coincidence: `UserError(msg, node)` takes the line and column from the node
/// and the FILE from the module currently being lowered, so a node reached by resolving a name
/// into another module would produce a caret at a real column of the wrong file. Measured for
/// these two files and it does not occur, including through the inline path, which saves and
/// restores currentSourcePath around the callee's body. The cross-module case is pinned in
/// tests/driver/test_module_diagnostic_file.py.
/// </summary>
public class StatementDiagnosticColumnTests
{
    private static CompilerError Fails(string src) =>
        Assert.Throws<CompilerError>(() =>
            new IRGenerator().Generate(
                new Parser(new Lexer("from pymcu.types import uint8\n" + src).Tokenize())
                    .ParseProgram(),
                new Dictionary<string, ProgramNode>(),
                new DeviceConfig { Arch = "avr" }));

    [Fact]
    public void ReturningAStringPointsAtTheStringItReturns()
    {
        //          123456789012
        // line 3: "    return \"nope\""  -- the literal starts at column 12
        var ex = Fails("def f() -> uint8:\n    return \"nope\"\ndef main() -> None:\n    b: uint8 = f()\n");

        Assert.Contains("cannot return a string", ex.Message);
        Assert.Equal(3, ex.Line);
        Assert.Equal(12, ex.Column);
    }

    [Fact]
    public void AnInstanceWithNoBoolPointsAtTheNameTested()
    {
        //          12345678
        // line 7: "    if c:"  -- `c` is at column 8
        var ex = Fails(
            "class C:\n    def __init__(self) -> None:\n        self.n: uint8 = 0\n" +
            "def main() -> None:\n    c = C()\n    if c:\n        pass\n");

        Assert.Contains("__bool__", ex.Message);
        Assert.Equal(8, ex.Column);
    }

    [Theory]
    //          12345
    // line 3: "    break"  -- a keyword statement IS its token, so it marks its own keyword.
    // Outside a loop, which is what makes it a refusal; inside one it is perfectly legal.
    // Line 3 and not 2 because Fails() prepends the types import.
    [InlineData("def main() -> None:\n    break\n", 3, 5, "Break statement")]
    [InlineData("def main() -> None:\n    continue\n", 3, 5, "Continue statement")]
    [InlineData("def main() -> None:\n    x: uint8 = 1\n    raise ValueError(x)\n", 4, 5,
                "is not a")]
    public void AKeywordStatementPointsAtItsOwnKeyword(string src, int line, int column,
                                                        string fragment)
    {
        var ex = Fails(src);

        Assert.Contains(fragment, ex.Message);
        Assert.Equal(line, ex.Line);
        Assert.Equal(column, ex.Column);
    }

    [Fact]
    public void AMissingReturnPointsAtTheDefKeyword()
    {
        // The `def`, not the return annotation. The message is about the whole function and
        // tells the reader to add a return "on the remaining path", which is not one token;
        // pointing at `-> uint8` would read as "change this type", the opposite of the remedy.
        var ex = Fails("def f() -> uint8:\n    x: uint8 = 1\ndef main() -> None:\n    b: uint8 = f()\n");

        Assert.Contains("can reach the end of its body", ex.Message);
        Assert.Equal(2, ex.Line);
        Assert.Equal(1, ex.Column);
        Assert.Equal(3, ex.Length);   // the keyword itself
    }

    [Fact]
    public void AMissingReturnOnAMETHODStillReportsNoColumn()
    {
        // Not a decision, a gap, and it is downstream of the parser: the AST carries the
        // method at L5 C5 (verified directly), but the node that reaches VisitFunction has
        // no position, so something in the class path replaces or copies it and drops the
        // stamp. That path is in Scan.cs, which this change deliberately does not touch.
        //
        // Written to FLIP: when the class path preserves the position this fails, and the fix
        // is to assert column 5, not to restore the silence.
        var ex = Fails(
            "class Box:\n    def __init__(self) -> None:\n        self.n: uint8 = 0\n" +
            "    def get(self) -> uint8:\n        x: uint8 = self.n\n" +
            "def main() -> None:\n    b = Box()\n    v: uint8 = b.get()\n");

        Assert.Contains("can reach the end of its body", ex.Message);
        Assert.False(ex.HasColumn);
    }
}
