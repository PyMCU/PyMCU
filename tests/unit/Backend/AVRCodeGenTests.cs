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

    // ─── Neg16 correctness ────────────────────────────────────────────────
    // 16-bit negation must emit NEG R24 / COM R25 / SBCI R25, 255.
    // The former (wrong) codegen used ADC R25, R1 which adds carry instead of
    // subtracting it, producing wrong results whenever the low byte is 0
    // (e.g. -0x0100 = 0xFE00 instead of the correct 0xFF00).

    [Fact]
    public void UnaryNeg16_EmitsNegComSbci_NotAdc()
    {
        var prog = new ProgramIR();
        prog.Functions.Add(new Function
        {
            Name = "main",
            Body =
            [
                new Unary(PyMCU.IR.UnaryOp.Neg,
                    new Variable("x", DataType.INT16),
                    new Variable("y", DataType.INT16)),
                new Return(new NoneVal())
            ]
        });

        var asm = Compile(prog);

        Assert.Contains("NEG\tR24", asm);
        Assert.Contains("COM\tR25", asm);
        // The carry-correcting instruction must be SBCI R25, 255, NOT ADC R25, R1.
        Assert.Contains("SBCI\tR25, 255", asm);
        Assert.DoesNotContain("ADC\tR25, R1", asm);
    }

    [Fact]
    public void UnaryNeg16_DoesNotUseAdcR1ForCarryCorrection()
    {
        // Regression guard: ADC R25, R1 after NEG R24 + COM R25 adds carry when
        // it should subtract carry. For the value 0x0100 (lo=0, C=0 after NEG):
        //   Wrong:  COM R25 + ADC R25, R1 -> ~0x01 + 0 = 0xFE  (gives 0xFE00, not 0xFF00)
        //   Correct: COM R25 + SBCI R25,255 -> ~0x01 + 1 - 0 = 0xFF (gives 0xFF00) ✓
        var prog = new ProgramIR();
        prog.Functions.Add(new Function
        {
            Name = "main",
            ReturnType = DataType.INT16,
            Body =
            [
                new Unary(PyMCU.IR.UnaryOp.Neg,
                    new Variable("x", DataType.INT16),
                    new Variable("y", DataType.INT16)),
                new Return(new Variable("y", DataType.INT16))
            ]
        });

        var asm = Compile(prog);

        Assert.DoesNotContain("ADC\tR25, R1", asm);
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

    // ─── ISR context save/restore: R0 and R1 ─────────────────────────────
    // R0 is clobbered by every MUL instruction.
    // R1 is the zero register relied on by SBC/ADC patterns after MUL.
    // Both must be saved in the ISR prologue and restored in the epilogue,
    // matching what avr-gcc generates for every ISR.

    [Fact]
    public void IsrContextSave_PushesR0AndR1()
    {
        var prog = new ProgramIR();
        prog.Functions.Add(new Function { Name = "main", Body = [new Return(new NoneVal())] });
        prog.Functions.Add(new Function
        {
            Name = "timer_isr",
            IsInterrupt = true,
            InterruptVector = 0x14,
            Body = [new Return(new NoneVal())]
        });

        var asm = Compile(prog);

        // The ISR prologue must push R0 and R1 to preserve the interrupted context.
        Assert.Contains("PUSH\tR0", asm);
        Assert.Contains("PUSH\tR1", asm);
    }

    [Fact]
    public void IsrContextRestore_PopsR0AndR1()
    {
        var prog = new ProgramIR();
        prog.Functions.Add(new Function { Name = "main", Body = [new Return(new NoneVal())] });
        prog.Functions.Add(new Function
        {
            Name = "timer_isr",
            IsInterrupt = true,
            InterruptVector = 0x14,
            Body = [new Return(new NoneVal())]
        });

        var asm = Compile(prog);

        Assert.Contains("POP\tR0", asm);
        Assert.Contains("POP\tR1", asm);
    }

    [Fact]
    public void IsrContextSave_ClearsR1AfterPush()
    {
        // Inside the ISR body R1 must equal 0 so that patterns like
        // "ADC Rd, R1" and "SBC Rd, R1" work correctly even if main's
        // MUL left R1 != 0 at the moment the interrupt fired.
        var prog = new ProgramIR();
        prog.Functions.Add(new Function { Name = "main", Body = [new Return(new NoneVal())] });
        prog.Functions.Add(new Function
        {
            Name = "timer_isr",
            IsInterrupt = true,
            InterruptVector = 0x14,
            Body = [new Return(new NoneVal())]
        });

        var asm = Compile(prog);

        // CLR R1 must appear in the ISR section (after the PUSH R1 save).
        Assert.Contains("CLR\tR1", asm);
    }

    // ─── 32-bit NEG ───────────────────────────────────────────────────────
    // int32 negation must emit NEG R24 / COM+SBCI R25 / COM+SBCI R22 / COM+SBCI R23.

    [Fact]
    public void UnaryNeg32_EmitsFullCarryChain()
    {
        var prog = new ProgramIR();
        prog.Functions.Add(new Function
        {
            Name = "main",
            Body =
            [
                new Unary(PyMCU.IR.UnaryOp.Neg,
                    new Variable("x", DataType.INT32),
                    new Variable("y", DataType.INT32)),
                new Return(new NoneVal())
            ]
        });

        var asm = Compile(prog);

        // Every byte after the first needs COM + SBCI 255 for carry propagation.
        Assert.Contains("NEG\tR24", asm);
        Assert.Contains("COM\tR25", asm);
        Assert.Contains("COM\tR22", asm);
        Assert.Contains("COM\tR23", asm);
        // All three high bytes need SBCI 255 for the carry correction.
        var sbciCount = asm.Split('\n').Count(l => l.Trim().StartsWith("SBCI\tR") && l.Contains("255"));
        Assert.True(sbciCount >= 3, $"Expected ≥ 3 SBCI Rn, 255 instructions; found {sbciCount}");
    }

    // ─── 32-bit BitNot ────────────────────────────────────────────────────

    [Fact]
    public void UnaryBitNot32_ComsAllFourBytes()
    {
        var prog = new ProgramIR();
        prog.Functions.Add(new Function
        {
            Name = "main",
            Body =
            [
                new Unary(PyMCU.IR.UnaryOp.BitNot,
                    new Variable("x", DataType.UINT32),
                    new Variable("y", DataType.UINT32)),
                new Return(new NoneVal())
            ]
        });

        var asm = Compile(prog);

        Assert.Contains("COM\tR24", asm);
        Assert.Contains("COM\tR25", asm);
        Assert.Contains("COM\tR22", asm);
        Assert.Contains("COM\tR23", asm);
    }

    // ─── JumpIfNotZero 16-bit: no redundant TST ───────────────────────────
    // OR R24, R25 sets the Z flag; emitting TST R24 afterwards is a wasted cycle.

    [Fact]
    public void JumpIfNotZero16_NoTstAfterOr()
    {
        var prog = MakeProgram("main",
            new JumpIfNotZero(new Variable("x", DataType.UINT16), "done"),
            new Label("done"),
            new Return(new NoneVal()));

        var asm = Compile(prog);

        Assert.Contains("OR\tR24, R25", asm);
        // TST R24 after OR R24, R25 is redundant and must not appear in the 16-bit path.
        // We check the OR appears and that no spurious TST follows it.
        var lines = asm.Split('\n').Select(l => l.Trim()).ToList();
        int orIdx = lines.FindIndex(l => l.StartsWith("OR\tR24, R25"));
        Assert.True(orIdx >= 0);
        // The instruction immediately after OR must NOT be TST R24.
        if (orIdx + 1 < lines.Count)
            Assert.False(lines[orIdx + 1].StartsWith("TST\tR24"),
                "TST R24 after OR R24,R25 is redundant and must be removed");
    }

    // ─── ADIW / SBIW optimization for 16-bit immediate add/sub ───────────
    // For constants in 1..63, the codegen must emit ADIW (1 word / 2 cycles)
    // instead of SUBI + SBCI (2 words / 4 cycles).

    [Fact]
    public void Binary_Uint16_Add_SmallConst_EmitsADIW()
    {
        // x + 5: should use ADIW R24, 5 (1 word) not SUBI+SBCI (2 words)
        var prog = MakeProgram("main",
            new Binary(IrBinaryOp.Add,
                new Variable("x", DataType.UINT16),
                new Constant(5),
                new Variable("y", DataType.UINT16)));

        var asm = Compile(prog);

        Assert.Contains("ADIW\tR24, 5", asm);
        Assert.DoesNotContain("SUBI\tR24", asm);
        Assert.DoesNotContain("SBCI\tR25", asm);
    }

    [Fact]
    public void Binary_Uint16_Sub_SmallConst_EmitsSBIW()
    {
        // x - 10: should use SBIW R24, 10 not SUBI+SBCI
        var prog = MakeProgram("main",
            new Binary(IrBinaryOp.Sub,
                new Variable("x", DataType.UINT16),
                new Constant(10),
                new Variable("y", DataType.UINT16)));

        var asm = Compile(prog);

        Assert.Contains("SBIW\tR24, 10", asm);
        Assert.DoesNotContain("SUBI\tR24", asm);
    }

    [Fact]
    public void Binary_Uint16_Add_LargeConst_EmitsSubiSbci()
    {
        // x + 200: out of ADIW range (>63); must fall back to SUBI+SBCI
        var prog = MakeProgram("main",
            new Binary(IrBinaryOp.Add,
                new Variable("x", DataType.UINT16),
                new Constant(200),
                new Variable("y", DataType.UINT16)));

        var asm = Compile(prog);

        Assert.DoesNotContain("ADIW", asm);
        Assert.Contains("SUBI\tR24", asm);
        Assert.Contains("SBCI\tR25", asm);
    }

    // ─── div/mod dispatch by type size ────────────────────────────────────
    // The compiler must call __div16/__mod16 for uint16 and __div32/__mod32 for uint32.
    // Calling __div8 for 16-bit operands would silently truncate the dividend.

    [Fact]
    public void Div_Uint16_Calls_Div16_NotDiv8()
    {
        var prog = MakeProgram("main",
            new Binary(IrBinaryOp.Div,
                new Variable("a", DataType.UINT16),
                new Variable("b", DataType.UINT16),
                new Variable("c", DataType.UINT16)));

        var asm = Compile(prog);

        Assert.Contains("CALL\t__div16", asm);
        Assert.DoesNotContain("CALL\t__div8", asm);
    }

    [Fact]
    public void Mod_Uint16_Calls_Mod16_NotMod8()
    {
        var prog = MakeProgram("main",
            new Binary(IrBinaryOp.Mod,
                new Variable("a", DataType.UINT16),
                new Variable("b", DataType.UINT16),
                new Variable("c", DataType.UINT16)));

        var asm = Compile(prog);

        Assert.Contains("CALL\t__mod16", asm);
        Assert.DoesNotContain("CALL\t__mod8", asm);
    }

    [Fact]
    public void Div_Uint32_Calls_Div32()
    {
        var prog = MakeProgram("main",
            new Binary(IrBinaryOp.Div,
                new Variable("a", DataType.UINT32),
                new Variable("b", DataType.UINT32),
                new Variable("c", DataType.UINT32)));

        var asm = Compile(prog);

        Assert.Contains("CALL\t__div32", asm);
        Assert.DoesNotContain("CALL\t__div8", asm);
        Assert.DoesNotContain("CALL\t__div16", asm);
    }

    [Fact]
    public void Mod_Uint32_Calls_Mod32()
    {
        var prog = MakeProgram("main",
            new Binary(IrBinaryOp.Mod,
                new Variable("a", DataType.UINT32),
                new Variable("b", DataType.UINT32),
                new Variable("c", DataType.UINT32)));

        var asm = Compile(prog);

        Assert.Contains("CALL\t__mod32", asm);
        Assert.DoesNotContain("CALL\t__mod8", asm);
        Assert.DoesNotContain("CALL\t__mod16", asm);
    }

    [Fact]
    public void Div_Uint8_Calls_Div8()
    {
        // Regression: 8-bit div must still call __div8 after the dispatch change.
        var prog = MakeProgram("main",
            new Binary(IrBinaryOp.Div,
                new Variable("a", DataType.UINT8),
                new Variable("b", DataType.UINT8),
                new Variable("c", DataType.UINT8)));

        var asm = Compile(prog);

        Assert.Contains("CALL\t__div8", asm);
    }

    // ─── Float (soft-float IEEE 754) ──────────────────────────────────────

    [Fact]
    public void Float_Constant_EmitsIeee754Bytes()
    {
        // 3.14f in IEEE 754 single = 0x4048F5C3
        // R22=0xC3, R23=0xF5, R24=0x48, R25=0x40
        var fc = new FloatConstant(3.14);
        var dst = new Variable("r", DataType.FLOAT);
        var prog = MakeProgram("main",
            new Copy(fc, dst),
            new Return(new Constant(0)));

        var asm = Compile(prog);

        Assert.Contains("LDI\tR22, 195", asm);  // 0xC3 = 195
        Assert.Contains("LDI\tR23, 245", asm);  // 0xF5 = 245
        Assert.Contains("LDI\tR24, 72",  asm);  // 0x48 = 72
        Assert.Contains("LDI\tR25, 64",  asm);  // 0x40 = 64
    }

    [Fact]
    public void Float_Add_EmitsCallToFpAdd()
    {
        var a = new Variable("a", DataType.FLOAT);
        var b = new Variable("b", DataType.FLOAT);
        var c = new Variable("c", DataType.FLOAT);
        var prog = MakeProgram("main",
            new Binary(IrBinaryOp.Add, a, b, c));

        var asm = Compile(prog);

        Assert.Contains("CALL\t__fp_add", asm);
    }

    [Fact]
    public void Float_Sub_EmitsCallToFpSub()
    {
        var a = new Variable("a", DataType.FLOAT);
        var b = new Variable("b", DataType.FLOAT);
        var c = new Variable("c", DataType.FLOAT);
        var prog = MakeProgram("main",
            new Binary(IrBinaryOp.Sub, a, b, c));

        var asm = Compile(prog);

        Assert.Contains("CALL\t__fp_sub", asm);
    }

    [Fact]
    public void Float_Mul_EmitsCallToFpMul()
    {
        var a = new Variable("a", DataType.FLOAT);
        var b = new Variable("b", DataType.FLOAT);
        var c = new Variable("c", DataType.FLOAT);
        var prog = MakeProgram("main",
            new Binary(IrBinaryOp.Mul, a, b, c));

        var asm = Compile(prog);

        Assert.Contains("CALL\t__fp_mul", asm);
    }

    [Fact]
    public void Float_Div_EmitsCallToFpDiv()
    {
        var a = new Variable("a", DataType.FLOAT);
        var b = new Variable("b", DataType.FLOAT);
        var c = new Variable("c", DataType.FLOAT);
        var prog = MakeProgram("main",
            new Binary(IrBinaryOp.Div, a, b, c));

        var asm = Compile(prog);

        Assert.Contains("CALL\t__fp_div", asm);
    }

    [Fact]
    public void Float_Lt_EmitsCallToFpLt()
    {
        var a = new Variable("a", DataType.FLOAT);
        var b = new Variable("b", DataType.FLOAT);
        var r = new Variable("r", DataType.UINT8);
        var prog = MakeProgram("main",
            new Binary(IrBinaryOp.LessThan, a, b, r));

        var asm = Compile(prog);

        Assert.Contains("CALL\t__fp_lt", asm);
    }

    [Fact]
    public void Float_Gt_EmitsCallToFpGt()
    {
        var a = new Variable("a", DataType.FLOAT);
        var b = new Variable("b", DataType.FLOAT);
        var r = new Variable("r", DataType.UINT8);
        var prog = MakeProgram("main",
            new Binary(IrBinaryOp.GreaterThan, a, b, r));

        var asm = Compile(prog);

        Assert.Contains("CALL\t__fp_gt", asm);
    }

    [Fact]
    public void Float_Return_LoadsIntoR22R25()
    {
        // Returning a float variable must load into R22:R25 (soft-float return convention).
        // Copy gives the variable a stack slot; Return must then emit 4 LDD instructions.
        var fc = new FloatConstant(1.0);
        var a = new Variable("a", DataType.FLOAT);
        var prog = new ProgramIR();
        prog.Functions.Add(new Function
        {
            Name = "main",
            ReturnType = DataType.FLOAT,
            Body = [new Copy(fc, a), new Return(a)]
        });

        var asm = Compile(prog);

        // Stack load into R22 (byte 0 / LSB) and R25 (MSB) must appear.
        Assert.Contains("LDD\tR22,", asm);
        Assert.Contains("LDD\tR25,", asm);
    }

    [Fact]
    public void Float_Copy_LoadsAndStores()
    {
        // Copy from one float var to another goes through R22:R25
        var src = new Variable("src", DataType.FLOAT);
        var dst = new Variable("dst", DataType.FLOAT);
        var prog = MakeProgram("main",
            new Copy(src, dst));

        var asm = Compile(prog);

        // Load from src (LDD) and store to dst (STD) — both via R22:R25
        Assert.Contains("LDD\tR22,", asm);
        Assert.Contains("STD\t", asm);
        Assert.Contains(", R22", asm);
    }
}
