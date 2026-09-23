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
/// `self._mcp.DIFF_PINS.get((pos, neg), None)` -- a class-attribute dict reached through
/// a field-held instance, keyed by a tuple (adafruit_mcp3xxx's AnalogIn). Two gaps hid
/// behind the one spelling: dict methods dispatched only on a bare-name receiver, and
/// dict keys folded one value per entry, so a tuple key was refused as a runtime tuple.
/// </summary>
public class NestedMemberDictTests
{
    private const string Prelude =
        "from pymcu.chips.atmega328p import GPIOR0\n\n";

    private static ProgramIR Compile(string body) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(Prelude + body).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });

    private const string AdcPair =
        "class Adc:\n" +
        "    DIFF_PINS = {(0, 1): 0, (1, 0): 1, (2, 3): 2}\n\n" +
        "class Inp:\n" +
        "    def __init__(self, mcp, pos: int, neg: int):\n" +
        "        self._mcp = mcp\n" +
        "        self._setting = self._mcp.DIFF_PINS.get((pos, neg), 9)\n\n";

    [Fact]
    public void DictGetThroughFieldHeldInstanceWithConstantTupleKeyFolds()
    {
        var ir = Compile(AdcPair +
            "a = Adc()\n" +
            "i = Inp(a, 1, 0)\n");

        // The lookup folds: (1, 0) -> 1, stored into the flattened field.
        var store = ir.Functions.SelectMany(f => f.Body).OfType<Copy>()
            .Where(c => c.Dst is Variable v && v.Name.Contains("_setting")).ToList();
        Assert.Contains(store, c => c.Src is Constant { Value: 1 });
    }

    [Fact]
    public void DictGetTupleKeyMissReturnsDefault()
    {
        var ir = Compile(AdcPair +
            "a = Adc()\n" +
            "i = Inp(a, 3, 4)\n");

        var store = ir.Functions.SelectMany(f => f.Body).OfType<Copy>()
            .Where(c => c.Dst is Variable v && v.Name.Contains("_setting")).ToList();
        Assert.Contains(store, c => c.Src is Constant { Value: 9 });
    }

    [Fact]
    public void DictGetTupleKeyWithRuntimePartsComparesEachPart()
    {
        var ir = Compile(AdcPair +
            "a = Adc()\n" +
            "i = Inp(a, GPIOR0.value, GPIOR0.value)\n");

        // A runtime pair lowers to a compare chain: two JumpIfNotEqual per entry.
        var jumps = ir.Functions.SelectMany(f => f.Body).OfType<JumpIfNotEqual>().ToList();
        Assert.True(jumps.Count >= 4, $"expected a per-part compare chain, saw {jumps.Count} jumps");
    }

    [Fact]
    public void DeadTailAfterUnconditionalRaiseIsNotChecked()
    {
        // The isinstance guard raises for a non-Adc receiver; CPython never reaches the
        // field read on the wrong class. Before dead-tail skipping, the expansion kept
        // checking statements after the raise and reported 'Other' has no 'BITS'.
        var ir = Compile(
            "class Adc:\n" +
            "    BITS = 10\n\n" +
            "class Other:\n" +
            "    pass\n\n" +
            "class Inp:\n" +
            "    def __init__(self, mcp):\n" +
            "        if not isinstance(mcp, Adc):\n" +
            "            raise ValueError(\"not a sibling\")\n" +
            "        self._mcp = mcp\n" +
            "        self._shift = 16 - self._mcp.BITS\n\n" +
            "try:\n" +
            "    Inp(Other())\n" +
            "except ValueError:\n" +
            "    pass\n");

        Assert.NotNull(ir);
    }
}
