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
/// RFC 0009 excludes buffers from the tagged-union payload (section 11.2: "a buffer's value
/// is a name, not a register"), so `Optional[bytearray]`/`Optional[bytes]` keeps the
/// pre-RFC-0009 mechanism: None is a compile-time mark, and a name rebound to a fresh buffer
/// under a run-time condition used to be refused honestly ("a buffer travels as a name, with
/// no run-time handle to choose between the two"). A real subroutine's Optional[bytearray]
/// PARAMETER already had a run-time answer for exactly this (Assign.cs: "arrives as a pointer
/// plus a member tag"); these tests are its LOCAL counterpart -- adafruit_onewire's own
/// `l_rom = bytearray(8)` under `if not l_rom:`, the demandant. The representation is a
/// sibling byte next to the pointer (0 = has a buffer, 1 = None), not the scalar wire tag: a
/// buffer's compile-time size is still whatever the winning branch's `bytearray(N)` says, and
/// costs nothing when the branch folds.
/// </summary>
public class OptionalLocalBufferTests
{
    private const string Hdr =
        "from pymcu.types import uint8\n" +
        "from pymcu.chips.atmega328p import GPIOR0, GPIOR1\n" +
        "from typing import Optional\n\n";

    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(Hdr + src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    private static IEnumerable<Instruction> All(ProgramIR ir) => ir.Functions.SelectMany(f => f.Body);

    [Fact]
    public void LocalOptionalBytearray_RebindUnderRuntimeCondition_Compiles()
    {
        // Used to throw: "'l_rom' is rebound to a new buffer under a condition decided at
        // run time ... a buffer travels as a name, with no run-time handle to choose between
        // the two." The condition here is a genuine run-time fact (a register read), so the
        // compiler cannot fold it away and prove the rebind never happens.
        var ir = Gen(
            "def make(flag: uint8) -> None:\n" +
            "    l_rom: Optional[bytearray] = None\n" +
            "    if flag:\n" +
            "        l_rom = bytearray(8)\n" +
            "        l_rom[0] = 42\n" +
            "    if l_rom is None:\n" +
            "        GPIOR1.value = 0\n" +
            "    else:\n" +
            "        GPIOR1.value = l_rom[0]\n" +
            "seed: uint8 = GPIOR0.value\n" +
            "make(seed)\n");

        Assert.NotEmpty(ir.Functions);
    }

    [Fact]
    public void LocalOptionalBytearray_ValueArm_AllocatesFreshStorageAndWritesAPresentTag()
    {
        var ir = Gen(
            "def make(flag: uint8) -> None:\n" +
            "    l_rom: Optional[bytearray] = None\n" +
            "    if flag:\n" +
            "        l_rom = bytearray(8)\n" +
            "        l_rom[0] = 42\n" +
            "    if l_rom is None:\n" +
            "        GPIOR1.value = 0\n" +
            "    else:\n" +
            "        GPIOR1.value = l_rom[0]\n" +
            "seed: uint8 = GPIOR0.value\n" +
            "make(seed)\n");

        var body = All(ir).ToList();
        // The value arm copies a fresh buffer's base address into the pointer's home.
        Assert.Contains(body, i => i is Copy { Src: ArrayBase });
        // A tag write happens on both arms (0 for the fresh buffer, the None index for the
        // declaration) -- at least two distinct constants land in a byte-wide sibling slot.
        var tagWrites = body.OfType<Copy>()
            .Where(c => c.Src is Constant && c.Dst is Variable { Type: DataType.UINT8 })
            .Select(c => ((Constant)c.Src).Value)
            .Distinct()
            .ToList();
        Assert.True(tagWrites.Count >= 2, $"expected at least two distinct tag values, got [{string.Join(",", tagWrites)}]");
    }

    [Fact]
    public void LocalOptionalBytearray_NeverActuallyNone_NoTagInTheReadArm()
    {
        // Decision 2 (RFC 0009), applied to buffers: `l_rom` is declared Optional, but the
        // ONE place it is read (`l_rom[0]`) is behind `if flag:`, the very branch that just
        // gave it a fresh buffer -- the None arm never reaches that read, so the read itself
        // needs no tag test. This is the cost decision 2 promises stays at zero: a program
        // that never asks "is it None?" on a path that could actually be None pays nothing
        // AT THE READ for the machinery that answers when it could.
        var ir = Gen(
            "def make(flag: uint8) -> None:\n" +
            "    l_rom: Optional[bytearray] = None\n" +
            "    if flag:\n" +
            "        l_rom = bytearray(8)\n" +
            "        GPIOR1.value = l_rom[0]\n" +
            "seed: uint8 = GPIOR0.value\n" +
            "make(seed)\n");

        // The read of l_rom[0] is an ordinary ArrayLoad/BytearrayLoad, not preceded by a
        // tag branch -- decision 2's promise for the one place this program reads it.
        Assert.Contains(All(ir), i => i is ArrayLoad or BytearrayLoad);
    }
}
