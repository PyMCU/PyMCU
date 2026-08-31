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
/// PyMCU#246: an array passed to a nested `@inline` and walked with a RUNTIME index.
///
/// The array-ness was lost at the binding, not missing at the declaration. The scan resolves a
/// method call's receiver through `localVarTypes`, which is filled by exactly one thing -- a
/// local assigned a constructor call -- so `self` was never in it. `self._emit(b, 2)` therefore
/// resolved to nothing, and the propagation that hands a callee's runtime-indexed parameter back
/// to the caller's array never ran.
///
/// EVERY TEST HERE NEEDS A RUNTIME SEED. With a compile-time index the array is scalarised into
/// b__0, b__1, ... and nothing needs addressable storage, so the case under test disappears and
/// the program compiles. GPIOR0 is the seed; a constant would make all of this pass vacuously.
/// </summary>
public class ArrayPassedToAnInlineTests
{
    private static ProgramIR Compile(string body) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(
                "from pymcu.chips.atmega328p import GPIOR0, GPIOR1\n" +
                "from pymcu.types import uint8, uint32\n\n" + body).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });

    /// The array loads whose INDEX is a variable. A scalarised array produces none: it becomes
    /// separate variables and the subscript is resolved at compile time. So this is the property
    /// under test, and it is not the same as "it compiled".
    private static List<ArrayLoad> RuntimeIndexedLoads(ProgramIR p) =>
        p.Functions.SelectMany(f => f.Body).OfType<ArrayLoad>()
         .Where(l => l.Index is not Constant).ToList();

    private const string Walker =
        "    @inline\n" +
        "    def _emit(self, buf: uint8[8], total: uint32) -> None:\n" +
        "        j: uint32 = 0\n" +
        "        while j < total:\n" +
        "            GPIOR1.value = buf[j]\n" +
        "            j = j + 1\n";

    [Fact]
    public void ANestedSelfCallKeepsTheArrayAddressable()
    {
        // The reported case. `send` declares the buffer and hands it to `self._emit`, which
        // walks it with a runtime index.
        var ir = Compile(
            "class Radio:\n" +
            "    def __init__(self) -> None:\n        self._n: uint32 = 0\n\n" + Walker + "\n" +
            "    @inline\n" +
            "    def send(self, v: uint32) -> None:\n" +
            "        b: uint8[8] = [0] * 8\n" +
            "        b[0] = uint8(v)\n" +
            "        self._emit(b, 2)\n\n" +
            "def main() -> None:\n" +
            "    r = Radio()\n" +
            "    r.send(uint32(GPIOR0.value))\n");

        Assert.Single(RuntimeIndexedLoads(ir));
    }

    [Fact]
    public void TwoLevelsOfNestingAlsoKeepIt()
    {
        // The shape the CYW43 helpers have: do_ioctl -> write_iovar_n -> the walker. One level
        // was fixed by typing `self` in the scan; this one needed the RECURSIVE helper to follow
        // `self.m(...)` too, and its own comment said it handled "direct calls (non-method)".
        var ir = Compile(
            "class Radio:\n" +
            "    def __init__(self) -> None:\n        self._n: uint32 = 0\n\n" + Walker + "\n" +
            "    @inline\n" +
            "    def _mid(self, buf: uint8[8], total: uint32) -> None:\n" +
            "        self._emit(buf, total)\n\n" +
            "    @inline\n" +
            "    def send(self, v: uint32) -> None:\n" +
            "        b: uint8[8] = [0] * 8\n" +
            "        b[0] = uint8(v)\n" +
            "        self._mid(b, 2)\n\n" +
            "def main() -> None:\n" +
            "    r = Radio()\n" +
            "    r.send(uint32(GPIOR0.value))\n");

        Assert.Single(RuntimeIndexedLoads(ir));
    }

    [Fact]
    public void ANamedReceiverStillWorks()
    {
        // The control, and the case that was already correct: it is what proved the gap was
        // `self` specifically rather than method calls in general.
        var ir = Compile(
            "class Radio:\n" +
            "    def __init__(self) -> None:\n        self._n: uint32 = 0\n\n" + Walker + "\n" +
            "def main() -> None:\n" +
            "    r = Radio()\n" +
            "    b: uint8[8] = [0] * 8\n" +
            "    b[0] = uint8(GPIOR0.value)\n" +
            "    r._emit(b, 2)\n");

        Assert.Single(RuntimeIndexedLoads(ir));
    }

    [Fact]
    public void AnArrayWithNoDeclaredTypeIsStillRefused()
    {
        // The control that keeps the fix honest. `b = [1, 2, 3]` genuinely has no array type,
        // and marking every argument addressable would have made this compile too.
        var ex = Assert.ThrowsAny<CompilerError>(() => Compile(
            "def main() -> None:\n" +
            "    b = [1, 2, 3]\n" +
            "    i: uint8 = GPIOR0.value\n" +
            "    GPIOR1.value = b[i]\n"));

        Assert.Contains("not addressable at run time", ex.Message);
    }

    [Fact]
    public void TheMessageDoesNotClaimADeclaredArrayWasNeverDeclared()
    {
        // Still reachable: a method call on an instance held in a FIELD (`self._i.walk(b, 2)`)
        // is not resolved by the scan, so this shape is refused with `b` declared `uint8[8]`.
        // Measured and left unfixed -- the receiver is a MemberAccessExpr, not the plain `self`
        // this fix taught the scan about.
        //
        // What the message must not do is tell that reader to declare a type they already wrote,
        // which is what it did before #246.
        var ex = Assert.ThrowsAny<CompilerError>(() => Compile(
            "class Inner:\n" +
            "    def __init__(self) -> None:\n        self._k: uint32 = 0\n\n" +
            "    @inline\n" +
            "    def walk(self, buf: uint8[8], total: uint32) -> None:\n" +
            "        j: uint32 = 0\n" +
            "        while j < total:\n" +
            "            GPIOR1.value = buf[j]\n" +
            "            j = j + 1\n\n" +
            "class Outer:\n" +
            "    def __init__(self) -> None:\n        self._i = Inner()\n\n" +
            "    @inline\n" +
            "    def go(self, v: uint32) -> None:\n" +
            "        b: uint8[8] = [0] * 8\n" +
            "        b[0] = uint8(v)\n" +
            "        self._i.walk(b, 2)\n\n" +
            "def main() -> None:\n" +
            "    o = Outer()\n" +
            "    o.go(uint32(GPIOR0.value))\n"));

        Assert.DoesNotContain("has no declared array type", ex.Message);
        Assert.Contains("field of the class", ex.Message);
    }
}
