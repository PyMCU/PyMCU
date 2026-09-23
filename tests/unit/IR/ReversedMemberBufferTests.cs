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
/// `reversed(self.field)` over the two compile-time shapes a field can hold: a fixed
/// array (`self._gpio = bytearray(2)`, adafruit_74hc595's bitbang path) and a constant
/// sequence (`self._levels = [..]`). Both were refused -- reversed() only accepted
/// literal lists, names, and range(), never a member access.
///
/// The SPI-path sibling fix is covered too: `self._gpio = val` where `val` aliases a
/// module-level bytes literal filed under `main.g1` -- the sequence lookup stopped at
/// the bare alias terminal and missed the qualified key.
/// </summary>
public class ReversedMemberBufferTests
{
    private static ProgramIR Compile(string body) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(body).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });

    [Fact]
    public void ReversedOverMemberArrayLoadsDescending()
    {
        var ir = Compile(
            "class SR:\n" +
            "    def __init__(self):\n" +
            "        self._gpio = bytearray(2)\n\n" +
            "    def shift(self) -> int:\n" +
            "        total = 0\n" +
            "        for b in reversed(self._gpio):\n" +
            "            total = total + b\n" +
            "        return total\n\n" +
            "sr = SR()\n" +
            "r = sr.shift()\n");

        var loads = ir.Functions.SelectMany(f => f.Body).OfType<ArrayLoad>()
            .Where(l => l.ArrayName.EndsWith("sr__gpio")).ToList();
        Assert.Equal(2, loads.Count);
        // Descending order: the first load reads index 1, the second index 0.
        Assert.Equal(1, ((Constant)loads[0].Index).Value);
        Assert.Equal(0, ((Constant)loads[1].Index).Value);
    }

    [Fact]
    public void ReversedOverListFieldLoadsDescending()
    {
        var ir = Compile(
            "class L:\n" +
            "    def __init__(self):\n" +
            "        self._levels = [10, 20, 30]\n\n" +
            "    def last(self) -> int:\n" +
            "        total = 0\n" +
            "        for v in reversed(self._levels):\n" +
            "            total = total + v\n" +
            "        return total\n\n" +
            "l = L()\n" +
            "r = l.last()\n");

        var loads = ir.Functions.SelectMany(f => f.Body).OfType<ArrayLoad>()
            .Where(l => l.ArrayName.EndsWith("l__levels")).ToList();
        Assert.Equal(new[] { 2, 1, 0 },
            loads.Select(l => ((Constant)l.Index).Value).ToArray());
    }

    [Fact]
    public void FieldAssignedModuleArrayIterates()
    {
        // `g1` is filed under `main.g1`; the setter's parameter aliases to the bare
        // name `g1`. The field store must probe the module-qualified spelling --
        // without it `self._gpio = val` degraded to a scalar store and every later
        // iterate/enumerate over the field was refused (adafruit_74hc595 SPI path).
        var ir = Compile(
            "g1 = bytearray(2)\n\n" +
            "class SR:\n" +
            "    def __init__(self):\n" +
            "        self._gpio = bytearray(1)\n\n" +
            "    @property\n" +
            "    def gpio(self):\n" +
            "        return self._gpio\n\n" +
            "    @gpio.setter\n" +
            "    def gpio(self, val) -> None:\n" +
            "        self._gpio = val\n\n" +
            "    def shift(self) -> int:\n" +
            "        total = 0\n" +
            "        for b in self._gpio:\n" +
            "            total = total + b\n" +
            "        return total\n\n" +
            "sr = SR()\n" +
            "sr.gpio = g1\n" +
            "r = sr.shift()\n");

        var loads = ir.Functions.SelectMany(f => f.Body).OfType<ArrayLoad>()
            .Where(l => l.ArrayName.Contains("gpio") || l.ArrayName.EndsWith("g1")).ToList();
        Assert.Equal(2, loads.Count);
    }
}
