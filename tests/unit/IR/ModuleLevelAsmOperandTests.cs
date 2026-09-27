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
/// An asm() operand at module level was handed `main.x`, a variable no read of `x` resolves
/// to: `x: uint8 = 7; asm("add %0, %1", x, s + 5); print(x)` left the global untouched and
/// printed 7. A name no frame binds is the module global, and the assembly may write it, so
/// nothing known about its value survives the asm.
/// </summary>
public class ModuleLevelAsmOperandTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(
                "from pymcu.chips.atmega328p import GPIOR0, GPIOR1\n" +
                "from pymcu.types import uint8, inline, asm\n\n" + src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    private static List<Val> AsmOperands(ProgramIR ir) =>
        ir.Functions.SelectMany(f => f.Body).OfType<InlineAsm>()
          .SelectMany(a => (IEnumerable<Val>?)a.Operands ?? Array.Empty<Val>()).ToList();

    [Fact]
    public void ModuleLevelOperand_IsTheGlobal_AndIsNotFoldedAfterward()
    {
        var ir = Gen(
            "x: uint8 = 7\n" +
            "asm(\"inc %0\", x)\n" +
            "GPIOR1.value = x\n");

        Assert.Contains(AsmOperands(ir), o => o is Variable { Name: "x" });
        Assert.DoesNotContain(AsmOperands(ir), o => o is Variable { Name: "main.x" });
        var main = ir.Functions.Single(f => f.Name == "main");
        int asmAt = main.Body.FindIndex(i => i is InlineAsm);
        Assert.DoesNotContain(main.Body.Skip(asmAt + 1), i => i is Copy { Src: Constant { Value: 7 } });
    }

    [Fact]
    public void GlobalDeclaredInAFunction_IsTheGlobal()
    {
        var ir = Gen(
            "x: uint8 = 7\n" +
            "def h():\n" +
            "    global x\n" +
            "    asm(\"inc %0\", x)\n" +
            "h()\n" +
            "GPIOR1.value = x\n");

        Assert.Contains(AsmOperands(ir), o => o is Variable { Name: "x" });
    }

    [Fact]
    public void FunctionLocal_StaysTheLocal()
    {
        var ir = Gen(
            "def f(v: uint8) -> uint8:\n" +
            "    y: uint8 = v\n" +
            "    asm(\"inc %0\", y)\n" +
            "    return y\n" +
            "GPIOR1.value = f(GPIOR0.value)\n");

        Assert.Contains(AsmOperands(ir), o => o is Variable { Name: "f.y" });
    }
}
