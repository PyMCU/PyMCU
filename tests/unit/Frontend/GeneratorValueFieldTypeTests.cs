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
/// The generated `_value` field's declared type is inferred from every payload the
/// generator yields or returns. Before it recognised <see cref="FloatLiteral"/>, a
/// `yield 1.5` fell back to `uint32`: the field stored the right four bytes but the
/// consumer read them as an integer, so `for x in g(): print(x)` printed the payload's
/// raw word instead of the decimal.
/// </summary>
public class GeneratorValueFieldTypeTests
{
    private static ProgramIR Compile(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer("from pymcu.types import uint8\n" + src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });

    /// The writes to `_value` inside the generated poll body: each state's payload store
    /// is a StoreIndirect typed at the field's element type. (`_state` writes are the
    /// other StoreIndirects in there; they are Constants on a uint16 field.)
    private static List<StoreIndirect> Stores(ProgramIR p) =>
        Assert.Single(p.Functions, f => f.Name.EndsWith("_poll")).Body
            .OfType<StoreIndirect>().ToList();

    [Fact]
    public void AFloatPayloadDeclaresAFloatValueField()
    {
        var ir = Compile(
            "def g():\n" +
            "    yield 1.5\n" +
            "T = 0\n" +
            "def main() -> None:\n" +
            "    global T\n" +
            "    for x in g():\n" +
            "        T = x\n");

        Assert.Contains(Stores(ir), s => s.Elem == DataType.FLOAT && s.Src is FloatConstant);
    }

    [Fact]
    public void AFloatConsumerReadsTheFieldAsFloat()
    {
        var ir = Compile(
            "def g():\n" +
            "    yield 1.5\n" +
            "T = 0\n" +
            "def main() -> None:\n" +
            "    global T\n" +
            "    for x in g():\n" +
            "        T = x\n");

        var main = Assert.Single(ir.Functions, f => f.Name == "main");
        Assert.Contains(main.Body.OfType<LoadIndirect>(), l => l.Elem == DataType.FLOAT);
    }

    [Fact]
    public void ASmallIntPayloadStillNarrowsToUint8()
    {
        var ir = Compile(
            "def g():\n" +
            "    yield 7\n" +
            "T = 0\n" +
            "def main() -> None:\n" +
            "    global T\n" +
            "    for x in g():\n" +
            "        T = x\n");

        // A constant int payload folds its `_value` writes, so the narrowing shows in the
        // frame itself: _state(uint16) + _value(uint8) = a 3-byte machine, not the 6 a
        // uint32 fallback would allocate.
        Assert.Equal(3, ir.GlobalArrays["main.__gen0__slot"]);
    }

    [Fact]
    public void AFloatPayloadWidensTheFrameToFourBytes()
    {
        var ir = Compile(
            "def g():\n" +
            "    yield 1.5\n" +
            "T = 0\n" +
            "def main() -> None:\n" +
            "    global T\n" +
            "    for x in g():\n" +
            "        T = x\n");

        Assert.Equal(6, ir.GlobalArrays["main.__gen0__slot"]);
    }
}
