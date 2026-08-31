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
/// PyMCU#243: a generator used as a VALUE, and a `yield` inside an uncalled lambda.
///
/// Both COMPILED before this, which is what separates them from the rest of the generator
/// surface. `take(gen())` emitted a call whose argument occurred exactly once in the whole
/// MIR -- as that argument, never assigned -- and `f = lambda: (yield 1)` lowered `main` to a
/// debug line and a `ret` with the statement gone.
///
/// The cause is one line of design rather than a bug: a PyMCU instance is not a value, it is a
/// NAME with fields. An ordinary class survives an anonymous construction because its fields
/// carry the content; a generator's synthesized class has none, so the name has nothing behind
/// it. The legal consumption supplies a target, which is why `for v in gen()` works and every
/// value position does not.
/// </summary>
public class GeneratorAsValueTests
{
    private const string Gen =
        "def gen():\n" +
        "    yield 1\n" +
        "def take(g: uint8) -> None:\n" +
        "    pass\n";

    private static CompilerError Fails(string body) =>
        Assert.ThrowsAny<CompilerError>(() => Compile(body));

    private static void Compile(string body) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(
                "from pymcu.types import uint8\n" + body).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });

    [Theory]
    [InlineData("    take(gen())\n")]
    [InlineData("    take(gen() + 1)\n")]
    [InlineData("    gen()\n")]
    public void AGeneratorInAValuePositionIsRefused(string stmt)
    {
        var ex = Fails(Gen + "def main() -> None:\n" + stmt);

        Assert.Contains("a generator can only be consumed by `for`", ex.Message);
    }

    [Fact]
    public void AGeneratorReturnedFromAFunctionIsRefused()
    {
        var ex = Fails(Gen + "def make():\n    return gen()\ndef main() -> None:\n    make()\n");

        Assert.Contains("a generator can only be consumed by `for`", ex.Message);
    }

    [Fact]
    public void TheBareCallIsRefusedToo_AndThatIsStricterThanCPython()
    {
        // PINNED, and a deliberate choice rather than a side effect. `gen()` on its own is a
        // no-op in CPython: a generator is created and discarded. PyMCU refuses it.
        //
        // The alternative was a carve-out for the discarded case, which would have meant one
        // rule for a construction whose result is used and another for one whose result is not.
        // On a part with two kilobytes of RAM, a generator built and dropped is a mistake worth
        // naming, and one rule is easier to defend than the exception.
        //
        // If this is ever relaxed, the place to do it is the same branch: allow the throw when
        // the construction is an expression statement.
        var ex = Fails(Gen + "def main() -> None:\n    gen()\n");

        Assert.Contains("a generator can only be consumed by `for`", ex.Message);
    }

    [Fact]
    public void TheForLoopStillWorks()
    {
        // The control. Without it the three above pass for a compiler that refuses generators
        // outright, which is not the fix.
        Compile(Gen + "def main() -> None:\n    for v in gen():\n        pass\n");
    }

    [Fact]
    public void AMethodCallOnAGeneratorKeepsTheProtocolMessage()
    {
        // The value-position check must NOT pre-empt this one. `gen().send(1)` constructs a
        // generator in a value position, so the new refusal fired first and replaced a specific
        // diagnostic with a general one. Caught by CallColumnTests, pinned here as the rule.
        var ex = Fails(Gen + "def main() -> None:\n    v: uint8 = gen().send(1)\n");

        Assert.Contains("send()", ex.Message);
        Assert.DoesNotContain("a generator can only be consumed by `for`", ex.Message);
    }

    [Theory]
    [InlineData("    f = lambda: (yield 1)\n")]
    [InlineData("    f = lambda: 1 + (yield 2)\n")]
    [InlineData("    f = lambda a: (yield a)\n")]
    public void AYieldInsideAnUncalledLambdaIsRefused(string stmt)
    {
        // The body of a lambda is set aside and only walked at the CALL, so an uncalled one
        // reached no check at all. A called one was always refused correctly, by the same
        // diagnostic this now raises: one answer for one program, whichever way it is written.
        var ex = Fails("def main() -> None:\n" + stmt);

        Assert.Contains("'yield' is only supported in top-level plain functions", ex.Message);
    }

    [Fact]
    public void ALambdaWithoutAYieldStillCompiles()
    {
        // The control for the walk. A check that refused every lambda would pass all three
        // cases above.
        Compile("def main() -> None:\n    f = lambda: 1\n");
    }
}
