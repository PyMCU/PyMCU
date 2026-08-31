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
/// PyMCU#177, the call cluster. A diagnostic about a CALL points at the callee; one about an
/// ARGUMENT points at that argument.
///
/// The split follows #164: what the message blames is what the caret marks. "hex() expects
/// exactly one argument" is about the call, so it marks `hex`; "hex() argument must be a
/// compile-time constant" is about the argument, so it marks the argument.
///
/// These land far more often than the parser stamping did, and the reason is worth keeping: a
/// callee is a VariableExpr, which the parser already stamps, so pointing at it produces a real
/// column today. The arguments only produce one when their own node type is stamped, which is
/// why a literal argument still reports no column below.
/// </summary>
public class CallDiagnosticColumnTests
{
    private static CompilerError Fails(string body)
    {
        string src = "from pymcu.types import uint8\ndef main() -> None:\n" + body;
        return Assert.Throws<CompilerError>(() =>
            new IRGenerator().Generate(
                new Parser(new Lexer(src).Tokenize()).ParseProgram(),
                new Dictionary<string, ProgramNode>(),
                new DeviceConfig { Arch = "avr" }));
    }

    //          123456789
    // line 3: "    s = hex(1, 2)"  -- `hex` starts at column 9

    [Theory]
    [InlineData("    s = hex(1, 2)\n")]
    [InlineData("    n = len()\n")]
    [InlineData("    v = pow(2)\n")]
    [InlineData("    v = bin(1, 2)\n")]
    [InlineData("    v = chr(1, 2)\n")]
    [InlineData("    v = str(1, 2)\n")]
    public void AnArityErrorPointsAtTheCallee(string body)
    {
        var ex = Fails(body);

        Assert.Equal(3, ex.Line);
        Assert.Equal(9, ex.Column);
        Assert.Equal(3, ex.Length);   // the callee's own name, underlined
    }

    [Fact]
    public void AnArgumentErrorPointsAtTheArgumentAndNotAtTheCallee()
    {
        //          1234567890123456
        // line 4: "    s = hex(a + 1)"  -- the '+' of the argument is at column 15
        var ex = Fails("    a: uint8 = 5\n    s = hex(a + 1)\n");

        Assert.Equal(4, ex.Line);
        Assert.Equal(15, ex.Column);
    }

    [Fact]
    public void AStringArgumentErrorPointsAtThatString()
    {
        // int.from_bytes(bytes, endian) -- the endian argument is the second one, and a string
        // literal is stamped, so this marks the string rather than the call.
        var ex = Fails("    v = int.from_bytes(b\"\\x01\\x02\", \"middle\")\n");

        Assert.Contains("endian", ex.Message);
        Assert.True(ex.Column > 9,
            $"expected the caret on the endian argument, well right of the callee, got {ex.Column}");
    }

    [Fact]
    public void AnArgumentThatIsALiteralPointsAtTheLiteral()
    {
        //          1234567890123
        // line 3: "    v = sum(1)"  -- the `1` is at column 13
        //
        // This test was written asserting the OPPOSITE, that an IntegerLiteral carried no
        // position and so drew no caret, with the note that it would start passing a real
        // column the day literals were stamped. That day came in the very next change and the
        // test failed, which is the whole point of having written it that way: it converted
        // itself from a guard into a discriminator rather than sitting green through a change
        // in behaviour.
        var ex = Fails("    v = sum(1)\n");

        Assert.Contains("sum()", ex.Message);
        Assert.Equal(3, ex.Line);
        Assert.Equal(13, ex.Column);
    }

    [Fact]
    public void AnArgumentSynthesisedRatherThanWrittenPointsAtTheLiteralItCameFrom()
    {
        // Was AnArgumentSynthesisedRatherThanWrittenStillReportsNoColumn. Its own text named the
        // answer it was waiting for: "nothing to point at but the whole literal". Stamping
        // ListExpr made the list that WRAPS those decoded bytes carry the position of the
        // `b"..."` token they came from, so the whole literal is now exactly what is marked.
        //
        // The rule it guarded is unchanged and still holds: the ELEMENTS are synthesised and
        // remain unstamped, because no element is anything the user typed. What changed is that
        // the container is not synthesised, and it always had a token.
        //
        //                    1         2
        //          123456789012345678901234567890
        // line 3: "    v = int.from_bytes(b\"\\x01\", \"little\")"  -- the literal is at column 24
        var ex = Fails("    v = int.from_bytes(b\"\\x01\", \"little\")\n");

        Assert.Contains("at least 2 bytes", ex.Message);
        Assert.Equal(3, ex.Line);
        Assert.Equal(24, ex.Column);
        Assert.Equal(7, ex.Length);   // `b"\\x01"` as written, quotes and escape included
    }
}
