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
/// `a[-1]` is the last element in Python. The named-array READ normalized a negative
/// constant index, but the stores and every access through a field (`self.buf[-1]`) handed
/// the negative constant to the backend as it was: the load read the bytes in FRONT of the
/// array and printed them, silently, and the store failed in the assembler on a negative
/// displacement. An out-of-range constant store was not checked at all, and wrote past the
/// array.
/// </summary>
public class ArrayNegativeIndexTests
{
    private const string Prelude =
        "from pymcu.chips.atmega328p import GPIOR0\n" +
        "from pymcu.types import uint8, uint16, inline\n\n";

    private static ProgramIR Generate(string body) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(Prelude + body).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });

    private static IEnumerable<Instruction> AllInstructions(ProgramIR ir) =>
        ir.Functions.SelectMany(f => f.Body);

    private static bool NoNegativeIndex(ProgramIR ir) =>
        !AllInstructions(ir).Any(i =>
            i is ArrayLoad { Index: Constant { Value: < 0 } }
            || i is ArrayStore { Index: Constant { Value: < 0 } });

    private const string FieldClass =
        "class R:\n" +
        "    def __init__(self):\n" +
        "        self.b: uint16[4] = [0] * 4\n";

    [Theory]
    [InlineData("")]
    [InlineData("    @inline\n")]
    public void AFieldReadAtMinusOne_LoadsTheLastElement(string decorator)
    {
        var ir = Generate(
            FieldClass +
            decorator +
            "    def last(self) -> uint16:\n" +
            "        return self.b[-1]\n" +
            "r = R()\n" +
            "r.b[3] = GPIOR0.value + 1000\n" +
            "x: uint16 = r.last()\n");

        Assert.True(NoNegativeIndex(ir));
        Assert.Contains(AllInstructions(ir), i => i is ArrayLoad { Index: Constant { Value: 3 } });
    }

    [Fact]
    public void AFieldStoreAndAugmentedStoreAtANegativeIndex_NameTheirElements()
    {
        var ir = Generate(
            FieldClass +
            "    def put(self, x: uint16):\n" +
            "        self.b[-1] = x\n" +
            "        self.b[-2] += x\n" +
            "r = R()\n" +
            "r.put(GPIOR0.value + 1000)\n");

        Assert.True(NoNegativeIndex(ir));
        Assert.Contains(AllInstructions(ir), i => i is ArrayStore { Index: Constant { Value: 3 } });
        Assert.Contains(AllInstructions(ir), i => i is ArrayStore { Index: Constant { Value: 2 } });
    }

    [Fact]
    public void ANamedStoreAndAugmentedStoreAtANegativeIndex_NameTheirElements()
    {
        var ir = Generate(
            "b: uint16[4] = [0] * 4\n" +
            "b[-1] = GPIOR0.value + 1000\n" +
            "b[-2] += GPIOR0.value + 2000\n" +
            "x: uint16 = b[3] + b[2]\n");

        Assert.True(NoNegativeIndex(ir));
        Assert.Contains(AllInstructions(ir), i => i is ArrayStore { Index: Constant { Value: 3 } });
        Assert.Contains(AllInstructions(ir), i => i is ArrayStore { Index: Constant { Value: 2 } });
    }

    [Theory]
    [InlineData("uint8", "x: uint8 = xs[-1]\n")]
    [InlineData("uint16", "xs[-2] = GPIOR0.value + 3000\n")]
    [InlineData("uint8", "xs[-1] += 10\n")]
    public void AHeapListAtANegativeIndex_CountsFromItsRunTimeLength(string elem, string statement)
    {
        // A list[T] element lives at base + 2 + index * size, and `xs[-1]` used -1 as it was:
        // it addressed the list's header and read or wrote its bytes as an element.
        var ir = Generate(
            $"xs: list[{elem}] = []\n" +
            "xs.append(GPIOR0.value + 5)\n" +
            "xs.append(GPIOR0.value + 6)\n" +
            statement);

        var main = Assert.Single(ir.Functions, f => f.Name == "main");
        Assert.DoesNotContain(main.Body, i => i is Binary b
            && (b.Src1 is Constant { Value: < 0 } || b.Src2 is Constant { Value: < 0 }));
        Assert.Contains(main.Body, i => i is Binary { Op: PyMCU.IR.BinaryOp.Sub, Src2: Constant { Value: > 0 } });
    }

    [Fact]
    public void AConstantStorePastTheEnd_IsAnIndexError()
    {
        var err = Assert.Throws<IndexError>(() => Generate(
            "b: uint16[4] = [0] * 4\n" +
            "b[4] = GPIOR0.value + 1\n" +
            "x: uint16 = b[0]\n"));
        Assert.Contains("out of range for size 4", err.Message);
    }

    [Fact]
    public void AFieldReadBeforeTheStart_IsAnIndexError()
    {
        var err = Assert.Throws<IndexError>(() => Generate(
            FieldClass +
            "    def bad(self) -> uint16:\n" +
            "        return self.b[-5]\n" +
            "r = R()\n" +
            "x: uint16 = r.bad()\n"));
        Assert.Contains("array index -5 out of range for size 4", err.Message);
    }
}
