using PyMCU.Backend.Targets.AVR;
using PyMCU.Common;
using PyMCU.Common.Models;
using PyMCU.IR;
using Xunit;
using IrBinaryOp = PyMCU.IR.BinaryOp;

namespace PyMCU.UnitTests;

public class AVRCodeGenTests
{
    private static readonly DeviceConfig Atmega328p = new() { Chip = "atmega328p", Arch = "avr" };

    private static string Compile(ProgramIR program, DeviceConfig? config = null)
    {
        var codegen = new AvrCodeGen(config ?? Atmega328p);
        var sw = new StringWriter();
        codegen.Compile(program, sw);
        return sw.ToString();
    }

    private static ProgramIR MakeProgram(string name, params Instruction[] body)
    {
        var prog = new ProgramIR();
        prog.Functions.Add(new Function { Name = name, Body = body.ToList() });
        return prog;
    }

    // ─── SimpleAddition ───────────────────────────────────────────────────

    [Fact]
    public void SimpleAddition()
    {
        // a = 1 + 2; return 0
        var prog = MakeProgram("main",
            new Binary(IrBinaryOp.Add, new Constant(1), new Constant(2), new Variable("a")),
            new Return(new Constant(0)));

        var asm = Compile(prog);

        // AVR: LDI R24, 1  then  SUBI R24, 254  (ADD immediate via SUBI -2 = SUBI 254)
        Assert.Contains("LDI\tR24, 1", asm);
        Assert.Contains("SUBI\tR24, 254", asm);
        // `a` is greedy-allocated to R4
        Assert.Contains("MOV\tR4, R24", asm);
    }

    // ─── IO Optimization ──────────────────────────────────────────────────

    [Fact]
    public void IOOptimization()
    {
        // PORTB (data 0x25) = 1 → OUT 0x05  (IO space: data - 0x20)
        // DDRB bit 0 (data 0x24) = 1 → SBI 0x04, 0
        var prog = MakeProgram("main",
            new Copy(new Constant(1), new MemoryAddress(0x25)),
            new BitSet(new MemoryAddress(0x24), 0));

        var asm = Compile(prog);

        Assert.Contains("OUT\t0x05, R24", asm);
        Assert.Contains("SBI\t0x04, 0", asm);
    }

    // ─── ImmediateArithmetic ──────────────────────────────────────────────

    [Fact]
    public void ImmediateArithmetic()
    {
        // x = x & 0xF0 → ANDI R24, 240
        var prog = MakeProgram("main",
            new Binary(IrBinaryOp.BitAnd,
                new Variable("x"), new Constant(0xF0),
                new Variable("x")));

        var asm = Compile(prog);

        Assert.Contains("ANDI\tR24, 240", asm);
    }

    // ─── PeepholeRedundantLDI ─────────────────────────────────────────────

    [Fact]
    public void PeepholeRedundantLDI()
    {
        // a = 1; b = 1; return
        // Two consecutive LDI R16, 1 → the second must be optimized away.
        var prog = MakeProgram("main",
            new Copy(new Constant(1), new Variable("a")),
            new Copy(new Constant(1), new Variable("b")),
            new Return(new NoneVal()));

        var asm = Compile(prog);

        var first = asm.IndexOf("LDI\tR16, 1", StringComparison.Ordinal);
        var second = first >= 0
            ? asm.IndexOf("LDI\tR16, 1", first + 1, StringComparison.Ordinal)
            : -1;

        Assert.Equal(-1, second);
    }
}
