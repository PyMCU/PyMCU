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
/// A parameter that only FORWARDS its buffer onward, never itself calling `len()`/`for-in`
/// on it, still needs a run-time length when the callee it forwards to does
/// (ResolveTransitiveRuntimeLenParams / ForwardRuntimeLenToInlineParam, Call.cs). The
/// closest reduction of adafruit_onewire's own `OneWireDevice.write(self, buf, ...):
/// self._bus.write(buf, ...)` calling `OneWireBus.write`'s own `len(buf)` -- a call on a
/// FIELD (`self._bus`), not on `self` directly, which is the shape this session's first
/// attempt (self-call only) missed.
/// </summary>
public class TransitiveRuntimeLenParamTests
{
    private const string Hdr =
        "from pymcu.types import uint8\n" +
        "from pymcu.chips.atmega328p import GPIOR0\n\n";

    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(Hdr + src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    [Fact]
    public void ForwardedBufferParameter_ThroughASelfFieldMethodCall_Compiles()
    {
        // Used to throw: `Middle.write`'s own "buf" parameter never calls len()/for-in
        // itself (it only forwards to `self._inner.write`), so ParamNeedsRuntimeLen's
        // direct-usage-only scan never flagged it, and `Inner.write`'s own hidden length
        // had no forwarded name to read at the outer call site.
        var ir = Gen(
            "class Inner:\n" +
            "    def write(self, buf: bytearray) -> uint8:\n" +
            "        return len(buf)\n" +
            "\n" +
            "class Middle:\n" +
            "    def __init__(self) -> None:\n" +
            "        self._inner = Inner()\n" +
            "\n" +
            "    def write(self, buf: bytearray) -> uint8:\n" +
            "        return self._inner.write(buf)\n" +
            "\n" +
            "m = Middle()\n" +
            "a: bytearray = bytearray(4)\n" +
            "b: bytearray = bytearray(9)\n" +
            "GPIOR0.value = m.write(a)\n" +
            "GPIOR0.value = m.write(b)\n");

        Assert.NotEmpty(ir.Functions);
    }
}
