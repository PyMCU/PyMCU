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
/// A module-level walrus stored into `main.x`, a slot no read of `x` resolves to:
/// `x = s + 1; print((0 + x) + (x := s + 2)); print(x)` printed `3 1` where CPython prints
/// `3 2`. With the store going to the global, two reads that happened too late surfaced:
/// the left operand of `+` was read after the walrus on its right had written it, and the
/// walrus handed back its variable instead of the value it stored.
/// </summary>
public class WalrusTargetScopeTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(
                "from pymcu.chips.atmega328p import GPIOR0, GPIOR1, GPIOR2\n" +
                "from pymcu.types import uint8\n\n" + src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    [Fact]
    public void CopyPropagation_DoesNotForwardAVariablePastItsRedefinition()
    {
        // `t0` holds x's value from before x is written again. Forwarding x into the add
        // (the copy `0 + x` simplifies to) read the new x on both sides.
        var prog = new ProgramIR();
        prog.Functions.Add(new Function
        {
            Name = "main",
            Body = new List<Instruction>
            {
                new Copy(new MemoryAddress(0x3E), new Variable("x", DataType.UINT8)),
                new Copy(new Variable("x", DataType.UINT8), new Temporary("t0", DataType.UINT8)),
                new Copy(new MemoryAddress(0x4A), new Variable("x", DataType.UINT8)),
                new Binary(PyMCU.IR.BinaryOp.Add, new Temporary("t0", DataType.UINT8),
                    new Variable("x", DataType.UINT8), new Temporary("t1", DataType.UINT16)),
                new Return(new Temporary("t1", DataType.UINT16)),
            },
        });
        var body = Optimizer.Optimize(prog).Functions[0].Body;

        Assert.DoesNotContain(body, i =>
            i is Binary { Src1: Variable { Name: "x" }, Src2: Variable { Name: "x" } });
    }
}
