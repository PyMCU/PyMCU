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
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU#392 and PyMCU#380. `bytearray(...)` is recognized -- and laid out as a fixed SRAM
/// buffer -- only from the two positions that reach VisitVarDecl: a bare local assignment
/// (`buf = bytearray(N)`) and its annotated spelling. A field-assignment TARGET
/// (`self.data = bytearray(...)` inside `__init__`, #392) and an inline CALL ARGUMENT
/// (`f(bytearray([...]))`, #380) both fall to the generic expression visitor instead, which
/// has no lowering for the bytearray() builtin and answers "a Python builtin that PyMCU does
/// not provide" -- true of no import adding it, false of what already works one binding away
/// (`buf = bytearray(N); self.data = buf` / `buf = bytearray([...]); f(buf)`).
///
/// Only a COMPILE-TIME-SIZED bytearray is in scope here (a literal count, or a list/bytes
/// literal whose length is known while compiling). `bytearray(n)` for a runtime `n` is a
/// different, arena-allocated shape and is not what either issue asks for.
/// </summary>
public class BytearrayPositionParityTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    // ── #392: bytearray() assigned to a FIELD inside __init__ ────────────────────────────

    [Fact]
    public void ABytearrayFieldAssignment_InsideInit_LaysOutAFixedBuffer()
    {
        // The issue's own minimal reproduction: a bytes-literal-sized buffer stored
        // straight into a field, never bound to a local first.
        var ir = Gen(
            "class Buf:\n" +
            "    def __init__(self):\n" +
            "        self.data = bytearray(b\"abcd\")\n" +
            "    def __len__(self):\n" +
            "        return 4\n" +
            "    def __getitem__(self, i):\n" +
            "        return self.data[i]\n" +
            "b = Buf()\n" +
            "def main():\n" +
            "    GPIOR0: ptr[uint8] = ptr(0x3E)\n" +
            "    GPIOR0.value = b[1]\n" +
            "    while True:\n        pass\n");

        // CPython: b"abcd"[1] is 'b' == 98.
        Assert.Contains(ir.Functions.SelectMany(f => f.Body).OfType<ArrayStore>(),
            s => s.Index is Constant k && k.Value == 1
                 && s.Src is Constant v && v.Value == (int)'b');
    }

    [Fact]
    public void ABytearrayFieldAssignment_WithAnIntegerSize_AlsoLaysOutAFixedBuffer()
    {
        Assert.NotNull(Gen(
            "class Buf:\n" +
            "    def __init__(self):\n" +
            "        self.data = bytearray(4)\n" +
            "    def fill(self):\n" +
            "        self.data[0] = 9\n" +
            "b = Buf()\n" +
            "def main():\n" +
            "    b.fill()\n" +
            "    while True:\n        pass\n"));
    }

    [Fact]
    public void ABytearrayLocal_BoundThenStoredIntoAField_StillWorks()
    {
        // The pre-existing, already-working shape (068_class_bytearray_field.py in the
        // oracle corpus): the guard that made #392 possible.
        Assert.NotNull(Gen(
            "class Holder:\n" +
            "    def __init__(self, buf):\n" +
            "        self.data = buf\n" +
            "buf = bytearray(3)\n" +
            "h = Holder(buf)\n" +
            "def main():\n" +
            "    h.data[0] = 1\n" +
            "    while True:\n        pass\n"));
    }

    // ── #380: bytearray() written INLINE as a call argument ──────────────────────────────

    [Fact]
    public void ABytearrayCallArgument_WrittenInline_IsAcceptedLikeANamedOne()
    {
        var ir = Gen(
            "def f(b) -> None:\n" +
            "    b[0] = 9\n" +
            "def main():\n" +
            "    f(bytearray([1, 2]))\n" +
            "    while True:\n        pass\n");

        // The literal's two elements are laid out as a real fixed buffer in the CALLER --
        // not refused, not silently dropped -- which is what #380 asks for: the same
        // recognition `buf = bytearray([1, 2]); f(buf)` already gets, reached this time
        // from an argument position instead of an assignment target.
        var hiddenBufferStores = ir.Functions.SelectMany(fn => fn.Body).OfType<ArrayStore>()
            .Where(s => s.ArrayName.EndsWith(".__inline_bytearray_arg0")).ToList();
        Assert.Contains(hiddenBufferStores, s => s.Index is Constant k && k.Value == 0 && s.Src is Constant v && v.Value == 1);
        Assert.Contains(hiddenBufferStores, s => s.Index is Constant k && k.Value == 1 && s.Src is Constant v && v.Value == 2);
    }

    [Fact]
    public void ABytearrayLocal_BoundThenPassed_StillWorks()
    {
        // The pre-existing workaround this issue's own repro names, kept green.
        Assert.NotNull(Gen(
            "def f(b) -> None:\n" +
            "    b[0] = 9\n" +
            "def main():\n" +
            "    buf = bytearray([1, 2])\n" +
            "    f(buf)\n" +
            "    while True:\n        pass\n"));
    }
}
