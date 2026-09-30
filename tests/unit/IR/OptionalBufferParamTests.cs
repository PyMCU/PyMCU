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
/// A REAL (non-inline) subroutine's `Optional[bytearray]`/`Optional[bytes]` parameter --
/// the parameter-crossing counterpart of OptionalLocalBufferTests' local rebind. RFC 0009
/// excludes buffers from the scalar tagged-union payload, so this is the same
/// pointer-plus-presence-byte mechanism, staged on the wire right after the pointer
/// (bufferOptionalParams / bufferOptionalParamTags, Statements.cs / Call.cs).
///
/// Demandant: adafruit_onewire's `_search_rom(self, l_rom: Optional[ReadableBuffer], diff)`,
/// a METHOD whose nested loops keep it from force-inlining. Covered here: the parameter is
/// read (both a real buffer and None reach it, from different call sites of the SAME
/// compiled body) and locally rebound under a run-time condition. NOT covered, and not
/// implemented: forwarding a rebound parameter's presence back through a TUPLE RETURN to
/// the caller's unpacked binding -- see the session's final report for the measured gap.
/// </summary>
public class OptionalBufferParamTests
{
    private const string Hdr =
        "from pymcu.types import uint8\n" +
        "from pymcu.chips.atmega328p import GPIOR0, GPIOR1\n" +
        "from typing import Optional\n\n";

    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(Hdr + src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    [Fact]
    public void RealFunctionOptionalBufferParam_GetsAPresenceTagParameter()
    {
        // Two call sites (one buffer, one None) so the body cannot get force-inlined into
        // a single compile-time-decidable answer -- a genuine real subroutine.
        var ir = Gen(
            "def touch(l_rom: Optional[bytearray], flag: uint8) -> None:\n" +
            "    i: uint8 = 0\n" +
            "    while i < 8:\n" +
            "        j: uint8 = 0\n" +
            "        while j < 8:\n" +
            "            j = j + 1\n" +
            "        i = i + 1\n" +
            "    if l_rom is None:\n" +
            "        GPIOR1.value = 255\n" +
            "    else:\n" +
            "        GPIOR1.value = l_rom[0]\n" +
            "seed: uint8 = GPIOR0.value\n" +
            "buf: bytearray = bytearray(8)\n" +
            "touch(buf, seed)\n" +
            "touch(None, seed)\n");

        var fn = ir.Functions.Single(f => f.Name == "touch");
        Assert.Equal(3, fn.Params.Count); // l_rom, l_rom$tag, flag
        Assert.Equal("touch.l_rom$tag", fn.Params[1]);
        Assert.NotNull(fn.TagParams);
        Assert.Contains(1, fn.TagParams!);
    }

    [Fact]
    public void RealFunctionOptionalBufferParam_RebindUnderRuntimeCondition_Compiles()
    {
        // Used to throw the same "buffer travels as a name" refusal a local gets: the
        // parameter was never registered in bytearrayParams/optionalTagSlots, so the
        // fresh-buffer rebind that already worked for a local (and, before this feature,
        // for a bytearrayParams-registered parameter with a manually-supplied tag) had no
        // gate to accept it through.
        var ir = Gen(
            "def touch(l_rom: Optional[bytearray], flag: uint8) -> None:\n" +
            "    i: uint8 = 0\n" +
            "    while i < 8:\n" +
            "        j: uint8 = 0\n" +
            "        while j < 8:\n" +
            "            j = j + 1\n" +
            "        i = i + 1\n" +
            "    if not l_rom:\n" +
            "        l_rom = bytearray(8)\n" +
            "    GPIOR1.value = l_rom[0]\n" +
            "seed: uint8 = GPIOR0.value\n" +
            "buf: bytearray = bytearray(8)\n" +
            "touch(buf, seed)\n" +
            "touch(None, seed)\n");

        Assert.NotEmpty(ir.Functions);
    }

    [Fact]
    public void CallSite_SendsAPresentTagForAConcreteBuffer_AndNoneForNone()
    {
        // The second call's argument is a NAME the compiler tracks as None (`empty`), not
        // the bare `None` literal: a literal argument to a real subroutine hits a separate,
        // pre-existing miscompilation this session found and reported but did not fix (the
        // call-site inliner proves the argument's member and, for a callee whose read is
        // behind a guard, still emits the guarded read's raise unconditionally -- see the
        // final report, `EmitGuardedTypeErrorRaise`/OptionalUnion.cs around line 4220). A
        // NAME argument avoids that path and isolates the tag-sending behaviour this test
        // is actually about.
        var ir = Gen(
            "def touch(l_rom: Optional[bytearray], flag: uint8) -> None:\n" +
            "    i: uint8 = 0\n" +
            "    while i < 8:\n" +
            "        j: uint8 = 0\n" +
            "        while j < 8:\n" +
            "            j = j + 1\n" +
            "        i = i + 1\n" +
            "    if l_rom is None:\n" +
            "        GPIOR1.value = 255\n" +
            "    else:\n" +
            "        GPIOR1.value = l_rom[0]\n" +
            "seed: uint8 = GPIOR0.value\n" +
            "buf: bytearray = bytearray(8)\n" +
            "empty: Optional[bytearray] = None\n" +
            "touch(buf, seed)\n" +
            "touch(empty, seed)\n");

        var calls = ir.Functions.SelectMany(f => f.Body).OfType<Call>()
            .Where(c => c.FunctionName == "touch").ToList();
        Assert.Equal(2, calls.Count);
        Assert.Equal(3, calls[0].Args.Count);
        Assert.Equal(3, calls[1].Args.Count);
        Assert.Equal(0, ((Constant)calls[0].Args[1]).Value); // buf: present
        // `empty` is itself a live Optional[bytearray] name, so its OWN tag forwards
        // (BufferPresenceTagVal's second case) rather than a fresh Constant.
        Assert.IsType<Variable>(calls[1].Args[1]);
    }
}
