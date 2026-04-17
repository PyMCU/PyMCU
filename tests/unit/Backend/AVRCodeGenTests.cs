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

    // ─── HighMemoryStore ──────────────────────────────────────────────────
    // Addresses > 0x5F must use STS (not OUT) for stores.

    [Fact]
    public void HighMemoryStore_UsesSTS()
    {
        // Data address 0x80 is outside the I/O range → must emit STS.
        var prog = MakeProgram("main",
            new Copy(new Constant(7), new MemoryAddress(0x80)));

        var asm = Compile(prog);

        Assert.Contains("STS\t0x0080, R24", asm);
    }

    // ─── HighMemoryLoad ───────────────────────────────────────────────────
    // Addresses > 0x5F must use LDS (not IN) for loads.

    [Fact]
    public void HighMemoryLoad_UsesLDS()
    {
        // Data address 0x80 is outside the I/O range → must emit LDS.
        var prog = MakeProgram("main",
            new Copy(new MemoryAddress(0x80), new Variable("x")),
            new Return(new Constant(0)));

        var asm = Compile(prog);

        Assert.Contains("LDS\tR24, 0x0080", asm);
        Assert.DoesNotContain("IN\t", asm);
    }

    // ─── IOSpaceLoad ──────────────────────────────────────────────────────
    // Addresses in 0x20–0x5F must use IN for loads.

    [Fact]
    public void IOSpaceLoad_UsesIN()
    {
        // PINB (data 0x23) → IN 0x03
        var prog = MakeProgram("main",
            new Copy(new MemoryAddress(0x23), new Variable("pins")),
            new Return(new Constant(0)));

        var asm = Compile(prog);

        Assert.Contains("IN\tR24, 0x03", asm);
    }

    // ─── BitClearIO ───────────────────────────────────────────────────────
    // BitClear on an I/O address (0x20–0x3F) must emit CBI.

    [Fact]
    public void BitClearIO_EmitsCBI()
    {
        // DDRB (data 0x24) bit 3 = 0 → CBI 0x04, 3
        var prog = MakeProgram("main",
            new BitClear(new MemoryAddress(0x24), 3));

        var asm = Compile(prog);

        Assert.Contains("CBI\t0x04, 3", asm);
    }

    // ─── BitSetIO ─────────────────────────────────────────────────────────
    // BitSet on an I/O address (0x20–0x3F) must emit SBI.

    [Fact]
    public void BitSetIO_EmitsSBI()
    {
        // PORTB (data 0x25) bit 5 = 1 → SBI 0x05, 5
        var prog = MakeProgram("main",
            new BitSet(new MemoryAddress(0x25), 5));

        var asm = Compile(prog);

        Assert.Contains("SBI\t0x05, 5", asm);
    }

    // ─── Uint16ReturnValue ────────────────────────────────────────────────
    // Return of a constant > 255 must fill both R24 (low byte) and R25 (high byte).

    [Fact]
    public void Uint16ReturnValue_FillsBothReturnRegisters()
    {
        // 300 = 0x012C → R24 = 0x2C (44), R25 = 0x01 (1)
        // The function must declare UINT16 return type so the codegen sizes the load.
        var prog = new ProgramIR();
        prog.Functions.Add(new Function
        {
            Name = "main",
            ReturnType = DataType.UINT16,
            Body = [new Return(new Constant(300))]
        });

        var asm = Compile(prog);

        Assert.Contains("LDI\tR24, 44", asm);  // low byte: 300 & 0xFF = 44
        Assert.Contains("LDI\tR25, 1", asm);   // high byte: (300 >> 8) & 0xFF = 1
    }

    // ─── BitSetHighMemory ─────────────────────────────────────────────────
    // BitSet on an address outside the SBI range (> 0x3F) must use LDS/ORI/STS.

    [Fact]
    public void BitSetHighMemory_UsesLdsOriSts()
    {
        // Address 0x60 is above the SBI/CBI range (0x20–0x3F).
        var prog = MakeProgram("main",
            new BitSet(new MemoryAddress(0x60), 2));

        var asm = Compile(prog);

        // Must not use SBI (only valid for 0x20–0x3F)
        Assert.DoesNotContain("SBI\t", asm);
        // Must use ORI to set the bit
        Assert.Contains("ORI\tR24, 4", asm);  // 1 << 2 = 4
    }

    // ─── BitClearHighMemory ───────────────────────────────────────────────
    // BitClear on an address outside the CBI range (> 0x3F) must use LDS/ANDI/STS.

    [Fact]
    public void BitClearHighMemory_UsesLdsAndiSts()
    {
        // Address 0x68 is above the SBI/CBI range.
        var prog = MakeProgram("main",
            new BitClear(new MemoryAddress(0x68), 1));

        var asm = Compile(prog);

        Assert.DoesNotContain("CBI\t", asm);
        // ANDI mask for clearing bit 1: ~(1<<1) & 0xFF = 0xFD = 253
        Assert.Contains("ANDI\tR24, 253", asm);
    }

    // ─── InlineAsm passthrough ────────────────────────────────────────────

    [Fact]
    public void InlineAsm_EmittedVerbatim()
    {
        var prog = MakeProgram("main",
            new InlineAsm("NOP"),
            new InlineAsm("NOP"),
            new Return(new NoneVal()));

        var asm = Compile(prog);

        // Count raw NOP lines (not in a comment)
        int nopCount = asm
            .Split('\n')
            .Count(line => line.Trim() == "NOP");
        Assert.True(nopCount >= 2, $"Expected ≥ 2 NOP lines, found {nopCount}");
    }

    // ─── UnaryNeg ─────────────────────────────────────────────────────────

    [Fact]
    public void UnaryNeg_EmitsNEG()
    {
        var prog = MakeProgram("main",
            new Unary(PyMCU.IR.UnaryOp.Neg, new Variable("x"), new Variable("y")));

        var asm = Compile(prog);

        Assert.Contains("NEG\tR24", asm);
    }

    // ─── UnaryBitNot ──────────────────────────────────────────────────────

    [Fact]
    public void UnaryBitNot_EmitsCOM()
    {
        var prog = MakeProgram("main",
            new Unary(PyMCU.IR.UnaryOp.BitNot, new Variable("x"), new Variable("y")));

        var asm = Compile(prog);

        Assert.Contains("COM\tR24", asm);
    }

    // ─── DuplicateISRVector ───────────────────────────────────────────────
    // Two ISRs on the same vector must cause a compile-time error.

    [Fact]
    public void DuplicateISRVector_ThrowsException()
    {
        var prog = new ProgramIR();
        prog.Functions.Add(new Function
        {
            Name = "main",
            Body = [new Return(new NoneVal())]
        });
        prog.Functions.Add(new Function
        {
            Name = "isr_a",
            IsInterrupt = true,
            InterruptVector = 0x02,
            Body = [new Return(new NoneVal())]
        });
        prog.Functions.Add(new Function
        {
            Name = "isr_b",
            IsInterrupt = true,
            InterruptVector = 0x02,  // same vector as isr_a
            Body = [new Return(new NoneVal())]
        });

        Assert.Throws<Exception>(() => Compile(prog));
    }

    // ─── ExternSymbols ────────────────────────────────────────────────────

    [Fact]
    public void ExternSymbols_EmittedWithDotExternDirective()
    {
        var prog = new ProgramIR();
        prog.ExternSymbols.Add("uart_puts");
        prog.Functions.Add(new Function
        {
            Name = "main",
            Body = [new Return(new NoneVal())]
        });

        var asm = Compile(prog);

        Assert.Contains(".extern uart_puts", asm);
    }
}
