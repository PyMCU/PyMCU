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
/// A real (non-inline) subroutine's `bytearray`/`bytes` parameter has no compile-time
/// length: there is no per-callsite monomorphization, so a function callers hand
/// differently-sized buffers to has always had to answer for every caller with the same
/// body. `len(param)` and `for x in param:` used to have no answer at all (`len() argument
/// must be a fixed-size array or list literal` / the for-in catch-all refusal) --
/// adafruit_onewire's own `crc8(data: ReadableBuffer)`, walked with `for byte in data`.
///
/// The fix mirrors Library mode's existing kernel ABI (a hidden length parameter appended
/// after the declared ones, `len(buf)` reading it) but per-parameter: only a parameter the
/// BODY actually measures (ParamNeedsRuntimeLen, Scan.cs) gets one, so a function that never
/// calls len()/for-in on its buffer parameter costs nothing extra.
/// </summary>
public class RuntimeBufferLengthTests
{
    private const string Hdr =
        "from pymcu.types import uint8\n" +
        "from pymcu.chips.atmega328p import GPIOR0, GPIOR1\n\n";

    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(Hdr + src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    private static IEnumerable<Instruction> All(ProgramIR ir) => ir.Functions.SelectMany(f => f.Body);

    [Fact]
    public void ParamMeasuredWithLen_GetsAHiddenLengthParameter()
    {
        var ir = Gen(
            "def size(data: bytearray) -> uint8:\n" +
            "    return len(data)\n" +
            "buf: bytearray = bytearray(6)\n" +
            "GPIOR1.value = size(buf)\n");

        var fn = ir.Functions.Single(f => f.Name == "size");
        Assert.Equal(2, fn.Params.Count);
        Assert.Equal("size.__len_data", fn.Params[1]);
    }

    [Fact]
    public void ParamWalkedWithForIn_GetsAHiddenLengthParameter()
    {
        var ir = Gen(
            "def crc8(data: bytearray) -> uint8:\n" +
            "    crc: uint8 = 0\n" +
            "    for byte in data:\n" +
            "        crc = crc ^ byte\n" +
            "    return crc\n" +
            "buf: bytearray = bytearray(6)\n" +
            "GPIOR1.value = crc8(buf)\n");

        var fn = ir.Functions.Single(f => f.Name == "crc8");
        Assert.Equal(2, fn.Params.Count);
        Assert.Equal("crc8.__len_data", fn.Params[1]);
        // A genuine indexed loop -- ArrayLoad through the pointer, not an unrolled body --
        // since the trip count is the hidden length, not a compile-time constant.
        Assert.Contains(fn.Body, i => i is ArrayLoad or BytearrayLoad);
    }

    [Fact]
    public void ParamNeverMeasured_StaysPlain_NoHiddenParameter()
    {
        var ir = Gen(
            "def touch(data: bytearray) -> uint8:\n" +
            "    return data[0]\n" +
            "buf: bytearray = bytearray(6)\n" +
            "GPIOR1.value = touch(buf)\n");

        var fn = ir.Functions.Single(f => f.Name == "touch");
        Assert.Single(fn.Params);
    }

    [Fact]
    public void CallSite_AppendsTheCallersOwnMeasuredLength_AfterEveryOtherArgument()
    {
        var ir = Gen(
            "def size(data: bytearray) -> uint8:\n" +
            "    return len(data)\n" +
            "a: bytearray = bytearray(4)\n" +
            "b: bytearray = bytearray(9)\n" +
            "GPIOR1.value = size(a)\n" +
            "GPIOR1.value = size(b)\n");

        var calls = All(ir).OfType<Call>().Where(c => c.FunctionName == "size").ToList();
        Assert.Equal(2, calls.Count);
        foreach (var c in calls) Assert.Equal(2, c.Args.Count);
        // Each call site's own length, not the callee's -- no per-callsite monomorphization
        // to fold it to, but the caller always knows its own buffer's size.
        Assert.Equal(4, ((Constant)calls[0].Args[1]).Value);
        Assert.Equal(9, ((Constant)calls[1].Args[1]).Value);
    }

    [Fact]
    public void CallSite_MeasuresItsOwnArgument_NotASimilarlyNamedGlobal()
    {
        // The shadowing trap GlobalShadowsBufferParamTests documents for the parameter name:
        // a module global named exactly like the parameter must not leak its length in.
        var ir = Gen(
            "def size(data: bytearray) -> uint8:\n" +
            "    return len(data)\n" +
            "data: bytearray = bytearray(13)\n" +
            "rb: bytearray = bytearray(17)\n" +
            "GPIOR1.value = size(rb)\n");

        var call = All(ir).OfType<Call>().Single(c => c.FunctionName == "size");
        Assert.Equal(17, ((Constant)call.Args[1]).Value);
    }
}
