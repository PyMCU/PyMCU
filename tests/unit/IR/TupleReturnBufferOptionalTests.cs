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

    private const string BusSrc =
        "class Bus:\n" +
        "    def _search_rom(self, l_rom: Optional[bytearray], diff: uint8) -> Tuple[bytearray, uint8]:\n" +
        "        if diff == 255:\n" +
        "            return None, 0\n" +
        "        rom: bytearray = bytearray(8)\n" +
        "        rom[0] = diff\n" +
        "        return rom, 0\n" +
        "\n" +
        "    def scan(self) -> uint8:\n" +
        "        diff: uint8 = 65\n" +
        "        rom = None\n" +
        "        count: uint8 = 0\n" +
        "        for _ in range(4):\n" +
        "            rom, diff = self._search_rom(rom, diff)\n" +
        "            if rom:\n" +
        "                count += 1\n" +
        "            if diff == 0:\n" +
        "                break\n" +
        "        return count\n" +
        "\n" +
        "bus = Bus()\n" +
        "GPIOR0.value = bus.scan()\n";

    [Fact]
    public void ABufferReturn_WritesTheSlotsPresenceByteToo()
    {
        // The return that answers a buffer must write the slot's "$tag" as well as the one that
        // answers None, or the byte keeps what an earlier call left and `if rom:` reads that
        // (ARM folded the loop to "never present"; AVR only worked by the order of the writes).
        var ir = Gen(BusSrc);
        var tagWrites = ir.Functions.SelectMany(f => f.Body).OfType<Copy>()
            .Where(c => c.Dst is Variable v && v.Name.Contains("iret") && v.Name.EndsWith("$tag"))
            .ToList();
        Assert.Contains(tagWrites, c => c.Src is Constant { Value: 1 });
        Assert.Contains(tagWrites, c => c.Src is Constant { Value: 0 });
    }

    [Fact]
    public void TheUnpackTarget_TakesTheCalleesPresenceByte_AndABufferPointerForTheNextCall()
    {
        var ir = Gen(BusSrc);
        var copies = ir.Functions.SelectMany(f => f.Body).OfType<Copy>().ToList();
        // The target's own byte comes from the slot's byte, not from a constant.
        Assert.Contains(copies, c => c.Dst is Variable { Name: var d } && d.EndsWith("scan.rom$tag")
                                     && c.Src is Variable { Name: var s } && s.Contains("iret"));
        // And the base of its buffer is left in a companion pointer for the call that takes it back.
        Assert.Contains(copies, c => c.Dst is Variable { Name: var d } && d.EndsWith("scan.rom$ptr")
                                     && c.Src is ArrayBase);
    }

    private const string ModuleLevelSrc =
        "class Bus:\n" +
        "    def search(self, l_rom: Optional[bytearray], diff: int) -> Tuple[bytearray, int]:\n" +
        "        if not l_rom:\n" +
        "            l_rom = bytearray(8)\n" +
        "        rom = bytearray(8)\n" +
        "        rom[0] = l_rom[0] + 5\n" +
        "        return rom, diff - 1\n" +
        "\n" +
        "bus = Bus()\n" +
        "rom = None\n" +
        "diff = 3\n" +
        "rom, diff = bus.search(rom, diff)\n" +
        "GPIOR0.value = rom[0]\n";

    [Fact]
    public void AModuleLevelNone_RebindFolded_DoesNotLeaveTheNameNoneForTheNextRead()
    {
        // The caller passes None, so `if not l_rom:` folds and no run-time branch reaches the
        // rebind; the name still answered for the None, and `l_rom[0]` raised TypeError.
        var main = Optimizer.Optimize(Gen(ModuleLevelSrc)).Functions.Single(f => f.Name == "main").Body;
        int begin = main.FindIndex(i => i is InlineExpansionMarker { IsEnd: false });
        int end = main.FindIndex(begin, i => i is InlineExpansionMarker { IsEnd: true });
        Assert.True(begin >= 0 && end > begin);
        Assert.DoesNotContain(main.GetRange(begin, end - begin), i => i is SignalError);
    }

    [Fact]
    public void AModuleLevelBufferTarget_IsDeclaredOnce()
    {
        // `rom` is a scalar (the None) and then an array (the buffer it is bound to): the
        // program has to declare one of them, or LLVM sees two definitions of @rom.
        var ir = Gen(ModuleLevelSrc);
        Assert.DoesNotContain(ir.Globals, g => g is Variable { Name: "rom" });
    }
}
