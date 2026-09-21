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
/// PyMCU#442: a class-level fixed buffer (`_BUFFER = bytearray(8)` in the class body --
/// CircuitPython's shared scratch-buffer idiom, adafruit_tcs34725) iterated through the
/// receiver. The storage is registered under the module-init-qualified spelling
/// (`main.C__BUFFER`), not the class-canonical name, so the iterable paths that only
/// resolved plain names and field-held constant sequences missed it.
///
/// Every test asserts on the ArrayLoad instructions: "it compiled" is not the property
/// under test -- a loop that resolved to the wrong storage would still compile and read
/// a placeholder scalar.
/// </summary>
public class ClassLevelBufferIterationTests
{
    private static ProgramIR Compile(string body) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(body).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });

    private static List<ArrayLoad> BufferLoads(ProgramIR p) =>
        p.Functions.SelectMany(f => f.Body).OfType<ArrayLoad>()
         .Where(l => l.ArrayName.EndsWith("C__BUFFER")).ToList();

    private const string BufferClass =
        "class C:\n" +
        "    _BUFFER = bytearray(8)\n\n";

    [Fact]
    public void ForInUnrollsClassLevelBuffer()
    {
        var ir = Compile(BufferClass +
            "    def read(self) -> int:\n" +
            "        total = 0\n" +
            "        for b in self._BUFFER:\n" +
            "            total = total + b\n" +
            "        return total\n\n" +
            "c = C()\n" +
            "r = c.read()\n");

        var loads = BufferLoads(ir);
        Assert.Equal(8, loads.Count);
        Assert.All(loads, l => Assert.Equal(8, l.Count));
    }

    [Fact]
    public void EnumerateUnrollsClassLevelBuffer()
    {
        var ir = Compile(BufferClass +
            "    def read(self) -> int:\n" +
            "        total = 0\n" +
            "        for i, b in enumerate(self._BUFFER):\n" +
            "            total = total + b\n" +
            "        return total\n\n" +
            "c = C()\n" +
            "r = c.read()\n");

        var loads = BufferLoads(ir);
        Assert.Equal(8, loads.Count);
    }

    [Fact]
    public void BufferPassedToInlinedMethodIteratesTheSharedStorage()
    {
        // The shape the idiom exists for: a method hands the shared buffer to another
        // method that walks it (`self.write(self._BUFFER)`). The parameter must alias
        // the class-level storage, not a per-instance name nothing ever wrote.
        var ir = Compile(BufferClass +
            "    def write(self, buf) -> None:\n" +
            "        self.last = 0\n" +
            "        for x in buf:\n" +
            "            self.last = self.last + x\n\n" +
            "    def flush(self) -> None:\n" +
            "        self.write(self._BUFFER)\n\n" +
            "c = C()\n" +
            "c.flush()\n");

        var loads = BufferLoads(ir);
        Assert.Equal(8, loads.Count);
    }

    [Fact]
    public void WholeAttributeReadResolvesToTheArrayStorage()
    {
        // `x = c._BUFFER` then `x[0]`: the read must name the shared array so the
        // subscript loads from it -- before the fix it produced a Variable on the
        // scalar placeholder `C__BUFFER` that nothing ever wrote.
        var ir = Compile(BufferClass +
            "c = C()\n" +
            "x = c._BUFFER\n" +
            "y = x[0]\n");

        var load = Assert.Single(BufferLoads(ir));
        Assert.Equal(8, load.Count);
    }
}
