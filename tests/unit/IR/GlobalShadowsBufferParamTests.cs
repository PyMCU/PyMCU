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
/// A module global named like a buffer parameter took the parameter's place, silently. The
/// scalar half of this was closed by 97aff8b2 (@inline) and 4368e1ad (plain def); these are
/// the buffer, list and register halves, one test per lookup that went wrong. Three causes:
///
/// 1. An @inline expansion at MODULE level runs inside `main`, and the lookups probe the
///    enclosing `main.&lt;name&gt;` before the expansion's own binding -- but a module-level
///    `buf = bytearray(3)` is registered as `main.buf` too, so `fill(rb, v)` wrote `buf`.
/// 2. A plain function's buffer parameter lives in bytearrayParams, which the module
///    fallbacks (the bare name, ModuleScopeArrayName) did not ask about.
/// 3. A module-level compile-time symbol (`buf: ptr[uint8] = ptr(0x4A)`) was resolved before
///    any frame binding was looked at.
///
/// The global in every program has a DIFFERENT size and address from the argument, so an
/// assertion about the argument cannot pass by reading the global.
/// </summary>
public class GlobalShadowsBufferParamTests
{
    private const string Prelude =
        "from pymcu.chips.atmega328p import GPIOR0, GPIOR1, GPIOR2\n" +
        "from pymcu.types import uint8, inline, ptr\n\n";

    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(Prelude + src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    private static IEnumerable<Instruction> All(ProgramIR ir) => ir.Functions.SelectMany(f => f.Body);

    private static bool IsGlobalBuf(string name) => name is "buf" or "main.buf";

    [Fact]
    public void InlineParam_IndexedStore_WritesTheArgument()
    {
        var ir = Gen(
            "@inline\n" +
            "def fill(buf: bytearray, i: uint8, v: uint8):\n" +
            "    buf[i] = v\n" +
            "buf = bytearray(3)\n" +
            "rb = bytearray(5)\n" +
            "fill(rb, GPIOR0.value, GPIOR0.value + 7)\n");

        var stores = All(ir).OfType<ArrayStore>().Where(s => s.Index is not Constant).ToList();
        Assert.Contains(stores, s => s.ArrayName is "rb" or "main.rb");
        Assert.DoesNotContain(stores, s => IsGlobalBuf(s.ArrayName));
    }

    [Fact]
    public void InlineParam_IndexedLoad_ReadsTheArgument()
    {
        // The reported read: a constant index into buffers that stay separate variables.
        var ir = Gen(
            "@inline\n" +
            "def first(buf: bytearray) -> uint8:\n" +
            "    return buf[0]\n" +
            "buf = bytearray(3)\n" +
            "buf[0] = GPIOR0.value + 11\n" +
            "rb = bytearray(5)\n" +
            "rb[0] = GPIOR0.value + 21\n" +
            "GPIOR1.value = first(rb)\n");

        static bool Reads(Instruction i, string storage) =>
            i is Copy { Src: Variable v } && (v.Name == storage + "__0" || v.Name == "main." + storage + "__0")
            || i is ArrayLoad al && (al.ArrayName == storage || al.ArrayName == "main." + storage);
        Assert.Contains(All(ir), i => Reads(i, "rb"));
        Assert.DoesNotContain(All(ir), i => Reads(i, "buf"));
    }

    [Fact]
    public void InlineParam_Len_MeasuresTheArgument()
    {
        // A list: a bytearray parameter's length was already asked of the binding first
        // (#512), the list and bytes spellings went on to `main.buf`.
        var ir = Gen(
            "@inline\n" +
            "def size(buf: list[uint8]) -> uint8:\n" +
            "    return len(buf)\n" +
            "buf = [GPIOR0.value, GPIOR0.value, GPIOR0.value]\n" +
            "rb = [GPIOR0.value, GPIOR0.value, GPIOR0.value, GPIOR0.value, GPIOR0.value]\n" +
            "GPIOR1.value = size(rb)\n");

        var copies = All(ir).OfType<Copy>().ToList();
        Assert.Contains(copies, c => c.Src is Constant { Value: 5 });
        Assert.DoesNotContain(copies, c => c.Src is Constant { Value: 3 });
    }

    [Fact]
    public void InlineParam_ForLoop_WalksTheArgument()
    {
        var ir = Gen(
            "@inline\n" +
            "def total(buf: bytearray) -> uint8:\n" +
            "    t: uint8 = 0\n" +
            "    for x in buf:\n" +
            "        t = t + x\n" +
            "    return t\n" +
            "buf = bytearray(3)\n" +
            "rb = bytearray(5)\n" +
            "rb[GPIOR0.value] = 9\n" +
            "GPIOR1.value = total(rb)\n");

        Assert.Contains(All(ir), i => i is ArrayLoad { ArrayName: "rb" or "main.rb" });
        Assert.DoesNotContain(All(ir), i => i is ArrayLoad al && IsGlobalBuf(al.ArrayName));
    }

    [Fact]
    public void InlineParam_Slice_CopiesTheArgument()
    {
        var ir = Gen(
            "@inline\n" +
            "def mid(buf: bytearray) -> uint8:\n" +
            "    t = buf[1:3]\n" +
            "    return t[0]\n" +
            "buf = bytearray(3)\n" +
            "rb = bytearray(5)\n" +
            "rb[GPIOR0.value] = 9\n" +
            "GPIOR1.value = mid(rb)\n");

        Assert.Contains(All(ir), i => i is ArrayLoad { ArrayName: "rb" or "main.rb" });
        Assert.DoesNotContain(All(ir), i => i is ArrayLoad al && IsGlobalBuf(al.ArrayName));
    }

    [Fact]
    public void PlainFunctionBufferParam_IndexedStore_GoesThroughThePointer()
    {
        var ir = Gen(
            "def fill(buf: bytearray, i: uint8, v: uint8):\n" +
            "    buf[i] = v\n" +
            "buf = bytearray(3)\n" +
            "rb = bytearray(5)\n" +
            "fill(rb, GPIOR0.value, GPIOR0.value + 7)\n");

        var fill = ir.Functions.Single(f => f.Name == "fill");
        Assert.Contains(fill.Body, i => i is BytearrayStore { PtrName: "fill.buf" });
        Assert.DoesNotContain(fill.Body, i => i is ArrayStore st && IsGlobalBuf(st.ArrayName));
    }

    [Fact]
    public void PlainFunctionBufferParam_Len_IsNotTheModuleArraysLength()
    {
        // A buffer reached by pointer has no compile-time length here, which is refused;
        // with a module array of the same name it compiled to that array's length.
        var ex = Record.Exception(() => Gen(
            "def size(buf: bytearray) -> uint8:\n" +
            "    return len(buf)\n" +
            "buf = bytearray(13)\n" +
            "rb = bytearray(17)\n" +
            "GPIOR1.value = size(rb)\n"));

        Assert.NotNull(ex);
    }
}
