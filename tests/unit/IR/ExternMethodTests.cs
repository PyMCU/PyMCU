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
/// PyMCU#229, the half that is not a dropped flag. `@extern` on a METHOD did not lose its
/// meaning, it acquired a different one:
///
///     externSymbols   []                  the C symbol was never registered
///     functions       [..., 'Box_bump']   the empty body WAS compiled
///
/// so the call reached a PyMCU function that does nothing and the C function was never called.
/// With `-> uint8` the missing-return check happens to stop the build; with `-> None` the
/// program builds clean and the hardware does nothing, which is the shape of it.
///
/// Refusing is the minimum honest answer and not the finished one. Supporting it needs the
/// class path to register the symbol and skip the body, which is separate work; until then the
/// only unacceptable option is the one that was there.
/// </summary>
public class ExternMethodTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });

    private const string Head =
        "from pymcu.chips.atmega328p import GPIOR0, GPIOR1\n" +
        "from pymcu.types import uint8\n\n";

    private const string Tail =
        "\n    while True:\n        pass\n";

    [Theory]
    // Returning None is the case that used to build clean all the way through.
    [InlineData("None", "        ...\n", "    b.bump()\n    GPIOR1.value = b.n\n")]
    // And returning a value, which stopped at a diagnostic about something else entirely: the
    // body it should never have been compiling in the first place.
    [InlineData("uint8", "        ...\n", "    GPIOR1.value = b.bump()\n")]
    public void AnExternMethodIsRefused(string ret, string body, string call)
    {
        var ex = Assert.ThrowsAny<CompilerError>(() => Gen(
            Head +
            "class Box:\n" +
            "    def __init__(self, n: uint8) -> None:\n" +
            "        self.n: uint8 = n\n\n" +
            "    @extern(\"c_bump\")\n" +
            $"    def bump(self) -> {ret}:\n" + body +
            "\ndef main() -> None:\n    b = Box(GPIOR0.value)\n" + call + Tail));

        Assert.Contains("@extern", ex.Message);
        Assert.Contains("module-level function", ex.Message);
        // The symbol by the name the program gave it, so the message names the thing that
        // would silently not have run.
        Assert.Contains("c_bump", ex.Message);
    }

    [Fact]
    public void AnExternModuleFunctionStillRegistersItsSymbol()
    {
        // The invariant. @extern at module level is the supported form and the refusal must not
        // reach it: it registers the symbol and compiles no body, which is what the assertions
        // below check rather than taking the absence of an exception as proof.
        var ir = Gen(Head +
                     "@extern(\"c_bump\")\n" +
                     "def bump(n: uint8) -> None:\n" +
                     "    ...\n" +
                     "\ndef main() -> None:\n" +
                     "    bump(GPIOR0.value)\n" +
                     "    GPIOR1.value = GPIOR0.value" + Tail);

        Assert.Contains("c_bump", ir.ExternSymbols);
        Assert.DoesNotContain(ir.Functions, f => f.Name == "bump");
    }
}
