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
/// RFC 0009 section 11, open question 2's tuple-return counterpart: a Tuple-returning
/// method's buffer-Optional return slot carries its own presence tag, so a caller's
/// tuple-unpack target inherits it -- OptionalBufferParamTests' own gap, left open in
/// that file's docs, closed here.
///
/// Demandant: adafruit_onewire's `OneWireBus.scan()` calling
/// `rom, diff = self._search_rom(rom, diff)` inside `for _ in range(0xFF):`, a REAL loop
/// (not unrolled) whose `rom` is a bare, unannotated local reassigned by the unpack every
/// iteration -- so its Optional-ness cannot be a compile-time fact the way two separate,
/// statically-distinct call sites' arguments are (OptionalBufferParamTests' own tests).
/// `_search_rom`'s tuple return (`Tuple[bytearray, int]`, no `Optional` in the
/// annotation) answers `None` for the buffer slot on an early `return None, 0`, and
/// `l_rom` is genuinely SUBSCRIPTED after its own rebind, so the compiler must resolve a
/// single storage for it -- exactly the case that threw "a buffer travels as a name"
/// before this feature: `rom`'s own tag was never registered, so the parameter it fed
/// into entered its rebind with nothing to dispatch on.
/// </summary>
public class TupleReturnBufferOptionalTests
{
    private const string Hdr =
        "from pymcu.types import uint8\n" +
        "from pymcu.chips.atmega328p import GPIOR0\n" +
        "from typing import Optional, Tuple\n\n";

    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(Hdr + src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    [Fact]
    public void LoopCarriedTupleUnpackTarget_FeedingABufferOptionalParameter_Compiles()
    {
        // Used to throw: `rom` (bare, unannotated, reassigned by the unpack every
        // iteration) never carried a tag, so `l_rom`'s own rebind inside _search_rom
        // (subscripted afterward, matching the real demandant) had nothing to dispatch
        // on and fell through to the pre-existing "buffer travels as a name" refusal.
        var ir = Gen(
            "class Bus:\n" +
            "    def _search_rom(self, l_rom: Optional[bytearray], diff: uint8) -> Tuple[bytearray, uint8]:\n" +
            "        if diff == 255:\n" +
            "            return None, 0\n" +
            "        if not l_rom:\n" +
            "            l_rom = bytearray(8)\n" +
            "        rom: bytearray = bytearray(8)\n" +
            "        rom[0] = l_rom[0]\n" +
            "        return rom, 0\n" +
            "\n" +
            "    def scan(self) -> uint8:\n" +
            "        diff: uint8 = 65\n" +
            "        rom = None\n" +
            "        count: uint8 = 0\n" +
            "        for _ in range(0xFF):\n" +
            "            rom, diff = self._search_rom(rom, diff)\n" +
            "            if rom:\n" +
            "                count += 1\n" +
            "            if diff == 0:\n" +
            "                break\n" +
            "        return count\n" +
            "\n" +
            "bus = Bus()\n" +
            "GPIOR0.value = bus.scan()\n");

        Assert.NotEmpty(ir.Functions);
    }

    [Fact]
    public void TupleReturnBufferSlot_EmitsAPresenceTagCopyForTheReturnedNone()
    {
        // The callee's own None-returning arm must write a "$tag" companion slot for
        // the buffer-shaped result position -- the wire this session added so the
        // caller's unpack target has something to inherit.
        var ir = Gen(
            "class Bus:\n" +
            "    def _search_rom(self, l_rom: Optional[bytearray], diff: uint8) -> Tuple[bytearray, uint8]:\n" +
            "        if diff == 255:\n" +
            "            return None, 0\n" +
            "        if not l_rom:\n" +
            "            l_rom = bytearray(8)\n" +
            "        rom: bytearray = bytearray(8)\n" +
            "        rom[0] = l_rom[0]\n" +
            "        return rom, 0\n" +
            "\n" +
            "    def scan(self) -> uint8:\n" +
            "        diff: uint8 = 65\n" +
            "        rom = None\n" +
            "        count: uint8 = 0\n" +
            "        for _ in range(0xFF):\n" +
            "            rom, diff = self._search_rom(rom, diff)\n" +
            "            if rom:\n" +
            "                count += 1\n" +
            "            if diff == 0:\n" +
            "                break\n" +
            "        return count\n" +
            "\n" +
            "bus = Bus()\n" +
            "GPIOR0.value = bus.scan()\n");

        var allCopies = ir.Functions.SelectMany(f => f.Body).OfType<Copy>().ToList();
        Assert.Contains(allCopies, c => c.Dst is Variable v && v.Name.EndsWith("$tag"));
    }
}
