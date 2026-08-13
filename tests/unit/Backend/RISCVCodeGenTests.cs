using System.Diagnostics;
using System.Text.RegularExpressions;
using PyMCU.Backend;
using PyMCU.Backend.Targets.RiscV;
using PyMCU.Common;
using PyMCU.Common.Models;
using PyMCU.IR;
using Xunit;

namespace PyMCU.UnitTests;

public class RISCVCodeGenTests
{
    private static readonly DeviceConfig Ch32v003 = new() { Chip = "ch32v003", Arch = "ch32v" };

    private static string Compile(ProgramIR program, DeviceConfig? config = null)
    {
        var codegen = new RiscvCodeGen(config ?? Ch32v003);
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

    // ─── SimpleReturn ─────────────────────────────────────────────────────────

    [Fact]
    public void SimpleReturn()
    {
        var prog = MakeProgram("my_func", new Return(new Constant(42)));
        var asm = Compile(prog);
        Assert.Contains("li\ta0, 42", asm);
        Assert.Contains("ret", asm);
    }

    // ─── MainInfiniteLoop ─────────────────────────────────────────────────────

    [Fact]
    public void MainInfiniteLoop()
    {
        // main must not have ret — it loops forever. The match is anchored on the
        // leading tab so it does not also catch the startup's `mret`.
        var prog = MakeProgram("main", new Return(new Constant(0)));
        var asm = Compile(prog);
        Assert.DoesNotContain("\tret", asm);
        Assert.Contains("j\tend_loop", asm);
        Assert.Contains("li\tsp, 0x20000800", asm);
    }

    // ─── NestedCallPrologue ───────────────────────────────────────────────────

    [Fact]
    public void NestedCallPrologue()
    {
        // caller is not a leaf → must save/restore ra
        var prog = MakeProgram("caller",
            new Call("other_func", [], new NoneVal()),
            new Return(new NoneVal()));
        var asm = Compile(prog);
        Assert.Contains("sw\tra,", asm);
        Assert.Contains("lw\tra,", asm);
    }

    // ─── SoftwareMul ─────────────────────────────────────────────────────────

    [Fact]
    public void SoftwareMul()
    {
        // a = b * c — ch32v003 has no mul; must call __mulsi3
        var prog = MakeProgram("main",
            new Binary(BinaryOp.Mul, new Variable("b"), new Variable("c"), new Variable("a")));
        var asm = Compile(prog);
        Assert.DoesNotContain("\tmul\t", asm);
        Assert.Contains("call\t__mulsi3", asm);
    }

    // ─── BinaryOps ────────────────────────────────────────────────────────────

    [Fact]
    public void BinaryOps()
    {
        // a = 10 + 20 → li t0, 10; addi t0, t0, 20; sw t0, -12(s0)
        var prog = MakeProgram("main",
            new Binary(BinaryOp.Add, new Constant(10), new Constant(20), new Variable("a")),
            new Return(new NoneVal()));
        var asm = Compile(prog);
        Assert.Contains("li\tt0, 10", asm);
        Assert.Contains("addi\tt0, t0, 20", asm);
        Assert.Contains("sw\tt0, -12(s0)", asm);
    }

    // ─── SubtractionOptimization ─────────────────────────────────────────────

    [Fact]
    public void SubtractionOptimization()
    {
        // a = b - 10 → addi t0, t0, -10
        var prog = MakeProgram("main",
            new Binary(BinaryOp.Sub, new Variable("b"), new Constant(10), new Variable("a")),
            new Return(new NoneVal()));
        var asm = Compile(prog);
        Assert.Contains("addi\tt0, t0, -10", asm);
    }

    // ─── BitManipulation ─────────────────────────────────────────────────────

    [Fact]
    public void BitManipulation()
    {
        // Set bit 5 of x → li t1, 32; or t0, t0, t1
        var prog = MakeProgram("main",
            new BitSet(new Variable("x"), 5),
            new Return(new NoneVal()));
        var asm = Compile(prog);
        Assert.Contains("li\tt1, 32", asm);
        Assert.Contains("or\tt0, t0, t1", asm);
    }

    // ─── FactorySupport ──────────────────────────────────────────────────────

    [Fact]
    public void FactoryRedirectsToTheExternalBackend()
    {
        // Codegen ships in pymcu-backend-riscv now, the same way AVR and PIC do,
        // so pymcuc itself only produces IR for this family.
        var ex = Assert.Throws<NotSupportedException>(
            () => CodeGenFactory.Create("ch32v003", Ch32v003));
        Assert.Contains("pymcuc-riscv", ex.Message);
    }

    [Fact]
    public void BackendProviderCreatesTheCodegen()
    {
        var provider = new RiscVBackendProvider();
        Assert.True(provider.Supports("ch32v003"));
        Assert.Equal("riscv", provider.Family);
        Assert.IsType<RiscvCodeGen>(provider.Create(Ch32v003));
    }

    // ─── Calling convention ──────────────────────────────────────────────────

    [Fact]
    public void CallPassesArgumentsInRegisters()
    {
        var prog = MakeProgram("main",
            new Call("add", [new Constant(7), new Constant(9)], new Variable("r")));
        var asm = Compile(prog);
        Assert.Contains("li\ta0, 7", asm);
        Assert.Contains("li\ta1, 9", asm);
        Assert.Contains("call\tadd", asm);
    }

    [Fact]
    public void CallReturnValueIsStoredFromA0()
    {
        var prog = MakeProgram("main",
            new Call("read", [], new Variable("r")));
        var asm = Compile(prog);
        Assert.Contains("sw\ta0, -12(s0)", asm);
    }

    [Fact]
    public void CallWithMoreArgumentsThanRegistersIsRejected()
    {
        // ilp32e only has a0-a5; stack-passed arguments are not implemented.
        List<Val> args = [
            new Constant(1), new Constant(2), new Constant(3),
            new Constant(4), new Constant(5), new Constant(6), new Constant(7)];
        var prog = MakeProgram("main", new Call("wide", args, new NoneVal()));

        var ex = Assert.Throws<NotSupportedException>(() => Compile(prog));
        Assert.Contains("ilp32e", ex.Message);
    }

    [Fact]
    public void ParametersAreSpilledToTheFrame()
    {
        var prog = new ProgramIR();
        prog.Functions.Add(new Function
        {
            Name = "add",
            Params = ["a", "b"],
            Body = [new Binary(BinaryOp.Add, new Variable("a"), new Variable("b"), new Variable("a")),
                    new Return(new Variable("a"))],
        });
        var asm = Compile(prog);
        Assert.Contains("sw\ta0, -12(s0)", asm);
        Assert.Contains("sw\ta1, -16(s0)", asm);
    }

    [Fact]
    public void IndirectCallGoesThroughJalr()
    {
        var prog = MakeProgram("main",
            new IndirectCall(new Variable("fp"), [new Constant(1)], new NoneVal()));
        var asm = Compile(prog);
        Assert.Contains("jalr\tt0", asm);
    }

    // ─── Globals ─────────────────────────────────────────────────────────────

    [Fact]
    public void GlobalsAreEmittedInBssAndAddressedBySymbol()
    {
        var prog = MakeProgram("main",
            new Copy(new Constant(1), new Variable("counter")),
            new Return(new NoneVal()));
        prog.Globals.Add(new Variable("counter"));

        var asm = Compile(prog);
        Assert.Contains(".section .bss", asm);
        Assert.Contains(".globl counter", asm);
        Assert.Contains("la\tt2, counter", asm);
        // A global must not also get a frame slot.
        Assert.DoesNotContain("sw\tt0, -12(s0)", asm);
    }

    [Fact]
    public void ProgramWithoutGlobalsEmitsNoBss()
    {
        var prog = MakeProgram("main", new Return(new NoneVal()));
        Assert.DoesNotContain(".section .bss", Compile(prog));
    }

    // ─── Startup ─────────────────────────────────────────────────────────────

    [Fact]
    public void StartupEmitsResetVectorAndEntersMain()
    {
        var prog = MakeProgram("main", new Return(new NoneVal()));
        var asm = Compile(prog);
        Assert.Contains("_vector_base:", asm);
        Assert.Contains("j\t_start", asm);
        Assert.Contains("csrw\tmtvec, t0", asm);
        Assert.Contains("la\tt0, main", asm);
        Assert.Contains("mret", asm);
    }

    [Fact]
    public void StackTopFollowsTheChipRamSize()
    {
        var prog = MakeProgram("main", new Return(new NoneVal()));
        var cfg = new DeviceConfig { Chip = "ch32v203", Arch = "ch32v", RamSize = 20480 };
        Assert.Contains("li\tsp, 0x20005000", Compile(prog, cfg));
    }

    [Fact]
    public void LibraryIrWithoutMainGetsNoStartup()
    {
        var prog = MakeProgram("helper", new Return(new Constant(0)));
        var asm = Compile(prog);
        Assert.DoesNotContain("_vector_base", asm);
        Assert.DoesNotContain("mret", asm);
    }

    // ─── Runtime helpers ─────────────────────────────────────────────────────

    [Fact]
    public void DivisionHelperIsEmittedWhenUsed()
    {
        // RV32EC has no M extension and the GCC multilib set ships no rv32ec
        // libgcc, so the helper has to be defined here or the link fails.
        var prog = MakeProgram("main",
            new Binary(BinaryOp.Div, Signed("a"), Signed("b"), Signed("c")));
        var asm = Compile(prog);
        Assert.Contains("call\t__floordivsi3", asm);
        Assert.Contains(".globl __floordivsi3", asm);
        Assert.Contains("__floordivsi3:", asm);
    }

    [Fact]
    public void HelpersAreOmittedWhenUnused()
    {
        var prog = MakeProgram("main",
            new Binary(BinaryOp.Add, new Variable("a"), new Variable("b"), new Variable("c")));
        var asm = Compile(prog);
        Assert.DoesNotContain("__divsi3", asm);
        Assert.DoesNotContain("__mulsi3", asm);
        Assert.DoesNotContain("__modsi3", asm);
    }

    // ─── Frame allocation ────────────────────────────────────────────────────

    [Fact]
    public void OperandsOfRelationalJumpsGetFrameSlots()
    {
        // These operands appear only in a comparison, an instruction shape the
        // shared stack allocator does not walk; without a slot they would be
        // emitted as undefined global symbols.
        var prog = MakeProgram("main",
            new JumpIfLessThan(new Variable("i"), new Variable("limit"), "done"),
            new Label("done"),
            new Return(new NoneVal()));
        var asm = Compile(prog);
        Assert.DoesNotContain("la\tt2, i", asm);
        Assert.DoesNotContain("la\tt2, limit", asm);
        Assert.Contains("(s0)", asm);
    }

    // ─── ISA compliance ──────────────────────────────────────────────────────

    [Fact]
    public void OnlyRv32eRegistersAreEmitted()
    {
        // RV32E halves the register file: x16-x31 do not exist.
        var prog = MakeProgram("main",
            new Call("f", [new Constant(1), new Constant(2)], new Variable("r")),
            new Binary(BinaryOp.Mul, new Variable("r"), new Constant(3), new Variable("r")),
            new Binary(BinaryOp.Mod, new Variable("r"), new Constant(7), new Variable("r")),
            new BitSet(new Variable("r"), 4),
            new Return(new Variable("r")));
        var asm = Compile(prog);

        var forbidden = new Regex(@"\b(a[67]|t[3-6]|s[2-9]|s1[01]|x(1[6-9]|2\d|3[01]))\b");
        var match = forbidden.Match(asm);
        Assert.False(match.Success, $"emitted register outside RV32E: {match.Value}");
    }

    // ─── Per-chip ISA profile ────────────────────────────────────────────────

    private static readonly DeviceConfig Ch32v203 = new() { Chip = "ch32v203", Arch = "riscv" };

    [Fact]
    public void Ch32v003TargetsTheEmbeddedIsa()
    {
        var asm = Compile(MakeProgram("main", new Return(new NoneVal())));
        Assert.Contains(".attribute arch, \"rv32ec_zicsr\"", asm);
        // ilp32e keeps the stack word-aligned.
        Assert.Contains(".attribute stack_align, 4", asm);
    }

    [Fact]
    public void Ch32v203TargetsTheFullIsa()
    {
        var asm = Compile(MakeProgram("main", new Return(new NoneVal())), Ch32v203);
        Assert.Contains(".attribute arch, \"rv32imac_zicsr\"", asm);
        Assert.Contains(".attribute stack_align, 16", asm);
    }

    [Fact]
    public void HardwareArithmeticReplacesTheHelpersOnV203()
    {
        var prog = MakeProgram("main",
            new Binary(BinaryOp.Mul, new Variable("a"), new Variable("b"), new Variable("c")),
            new Binary(BinaryOp.Div, Unsigned("a"), Unsigned("b"), Unsigned("d")),
            new Binary(BinaryOp.Mod, Unsigned("a"), Unsigned("b"), Unsigned("e")));
        var asm = Compile(prog, Ch32v203);

        Assert.Contains("mul\tt0, t0, t1", asm);
        // Unsigned division needs no flooring fix-up, so it stays in registers.
        Assert.Contains("divu\tt0, t0, t1", asm);
        Assert.Contains("remu\tt0, t0, t1", asm);
        Assert.DoesNotContain("__mulsi3", asm);
        Assert.DoesNotContain("__udivsi3", asm);
    }

    [Fact]
    public void SignedDivisionStillNeedsAHelperWhereDivisionIsHardware()
    {
        // No single instruction rounds toward negative infinity.
        var prog = MakeProgram("main",
            new Binary(BinaryOp.FloorDiv, Signed("a"), Signed("b"), Signed("c")),
            new Binary(BinaryOp.Mod, Signed("a"), Signed("b"), Signed("d")));
        var asm = Compile(prog, Ch32v203);
        Assert.Contains("call\t__floordivsi3", asm);
        Assert.Contains("call\t__floormodsi3", asm);
        // ...but the helper itself uses the hardware.
        Assert.Contains("div\ta2, a0, a1", asm);
        Assert.Contains("rem\ta0, a0, a1", asm);
    }

    [Fact]
    public void StackTopFollowsTheChipWhenNoRamSizeIsGiven()
    {
        // 20 KB on the V203 versus 2 KB on the V003.
        Assert.Contains("li\tsp, 0x20005000",
            Compile(MakeProgram("main", new Return(new NoneVal())), Ch32v203));
        Assert.Contains("li\tsp, 0x20000800",
            Compile(MakeProgram("main", new Return(new NoneVal()))));
    }

    [Fact]
    public void MultiplyOnV203DoesNotForceANonLeafFrame()
    {
        // Without a call there is no return address to preserve.
        var prog = MakeProgram("helper",
            new Binary(BinaryOp.Mul, new Variable("a"), new Variable("b"), new Variable("c")),
            new Return(new Variable("c")));
        Assert.DoesNotContain("sw\tra,", Compile(prog, Ch32v203));
    }

    // ─── Division semantics ──────────────────────────────────────────────────

    private static Variable Signed(string name) => new(name, DataType.INT32);
    private static Variable Unsigned(string name) => new(name, DataType.UINT32);

    [Theory]
    [InlineData(BinaryOp.Div)]
    [InlineData(BinaryOp.FloorDiv)]
    public void SignedDivisionFloorsLikePython(BinaryOp op)
    {
        // `/` and `//` are the same operation on integers, and both floor toward
        // negative infinity -- the semantics the AVR backend already implements.
        var prog = MakeProgram("main",
            new Binary(op, Signed("a"), Signed("b"), Signed("c")));
        var asm = Compile(prog);
        Assert.Contains("call\t__floordivsi3", asm);
        Assert.Contains("__floordivsi3:", asm);
    }

    [Fact]
    public void SignedRemainderTakesTheDivisorSign()
    {
        var prog = MakeProgram("main",
            new Binary(BinaryOp.Mod, Signed("a"), Signed("b"), Signed("c")));
        var asm = Compile(prog);
        Assert.Contains("call\t__floormodsi3", asm);
        Assert.Contains("__floormodsi3:", asm);
    }

    [Fact]
    public void UnsignedDivisionUsesTheUnsignedHelpers()
    {
        // Flooring and truncation agree when nothing can be negative, and the
        // signed routine would misread any operand above 2^31.
        var prog = MakeProgram("main",
            new Binary(BinaryOp.Div, Unsigned("a"), Unsigned("b"), Unsigned("c")),
            new Binary(BinaryOp.Mod, Unsigned("a"), Unsigned("b"), Unsigned("d")));
        var asm = Compile(prog);
        Assert.Contains("call\t__udivsi3", asm);
        Assert.Contains("call\t__umodsi3", asm);
        Assert.DoesNotContain("__floordivsi3", asm);
    }

    [Fact]
    public void ANegativeLiteralMakesTheContextSigned()
    {
        // Constant folding can lose the type; a negative operand still implies it.
        var prog = MakeProgram("main",
            new Binary(BinaryOp.Div, new Variable("a"), new Constant(-2), new Variable("c")));
        Assert.Contains("call\t__floordivsi3", Compile(prog));
    }

    // ─── Inline assembly ─────────────────────────────────────────────────────

    [Fact]
    public void InlineAsmWithoutOperandsIsEmittedVerbatim()
    {
        var prog = MakeProgram("main", new InlineAsm("\tnop"), new Return(new NoneVal()));
        Assert.Contains("\tnop", Compile(prog));
    }

    [Fact]
    public void InlineAsmSubstitutesItsOperands()
    {
        var prog = MakeProgram("main",
            new InlineAsm("\taddi %0, %1, 1", [new Variable("x"), new Variable("y")]),
            new Return(new NoneVal()));
        var asm = Compile(prog);
        Assert.Contains("\taddi a0, a1, 1", asm);
        // Read-write: loaded before the block and written back after it.
        Assert.Contains("lw\ta0,", asm);
        Assert.Contains("sw\ta0,", asm);
    }

    [Fact]
    public void InlineAsmWithTooManyOperandsIsRejected()
    {
        List<Val> operands = [
            new Variable("a"), new Variable("b"),
            new Variable("c"), new Variable("d"), new Variable("e")];
        var prog = MakeProgram("main", new InlineAsm("\tnop", operands));

        var ex = Assert.Throws<NotSupportedException>(() => Compile(prog));
        Assert.Contains("at most 4 operands", ex.Message);
    }

    [Fact]
    public void OutliningMarkersEmitNoCode()
    {
        var withMarkers = MakeProgram("main",
            new InlineExpansionMarker("helper", false),
            new Copy(new Constant(1), new Variable("x")),
            new InlineExpansionMarker("helper", true),
            new Return(new NoneVal()));
        var plain = MakeProgram("main",
            new Copy(new Constant(1), new Variable("x")),
            new Return(new NoneVal()));

        Assert.Equal(Compile(plain), Compile(withMarkers));
    }

    // ─── Arrays and flash data ───────────────────────────────────────────────

    [Fact]
    public void ArrayLoadScalesARuntimeIndexByTheElementSize()
    {
        var prog = MakeProgram("main",
            new ArrayLoad("table", new Variable("i"), new Variable("v"), DataType.UINT32, 4),
            new Return(new NoneVal()));
        var asm = Compile(prog);
        Assert.Contains("la\tt0, table", asm);
        Assert.Contains("slli\tt1, t1, 2", asm);
        Assert.Contains("add\tt0, t0, t1", asm);
        Assert.Contains("lw\tt1, 0(t0)", asm);
    }

    [Fact]
    public void ByteArraysNeedNoIndexScaling()
    {
        var prog = MakeProgram("main",
            new ArrayLoad("table", new Variable("i"), new Variable("v"), DataType.UINT8, 8),
            new Return(new NoneVal()));
        var asm = Compile(prog);
        Assert.DoesNotContain("slli", asm);
        Assert.Contains("lbu\tt1, 0(t0)", asm);
    }

    [Fact]
    public void ConstantIndexFoldsIntoAnOffset()
    {
        var prog = MakeProgram("main",
            new ArrayLoad("table", new Constant(3), new Variable("v"), DataType.UINT32, 4),
            new Return(new NoneVal()));
        var asm = Compile(prog);
        Assert.Contains("addi\tt0, t0, 12", asm);   // 3 * 4 bytes
        Assert.DoesNotContain("slli", asm);
    }

    [Fact]
    public void ArrayStoreUsesTheElementWidth()
    {
        var prog = MakeProgram("main",
            new ArrayStore("table", new Constant(0), new Variable("v"), DataType.UINT8, 4),
            new Return(new NoneVal()));
        Assert.Contains("sb\tt1, 0(t0)", Compile(prog));
    }

    [Fact]
    public void IndexedArraysAreReservedInBss()
    {
        var prog = MakeProgram("main",
            new ArrayStore("counters", new Constant(0), new Constant(1), DataType.UINT32, 4),
            new Return(new NoneVal()));
        var asm = Compile(prog);
        Assert.Contains(".section .bss", asm);
        Assert.Contains(".globl counters", asm);
        Assert.Contains("\t.zero\t16", asm);   // 4 elements x 4 bytes
    }

    [Fact]
    public void FlashDataLandsInRodataAsBytes()
    {
        var prog = MakeProgram("main",
            new FlashData("PATTERN", [1, 2, 4, 8]),
            new Return(new NoneVal()));
        var asm = Compile(prog);
        Assert.Contains(".section .rodata", asm);
        Assert.Contains("__flash_PATTERN:", asm);
        Assert.Contains("\t.byte\t1, 2, 4, 8", asm);
    }

    [Fact]
    public void FlashReadsAreOrdinaryLoads()
    {
        // Flash is in the same address space here, unlike AVR's LPM.
        var prog = MakeProgram("main",
            new ArrayLoadFlash("PATTERN", new Variable("i"), new Variable("v")),
            new Return(new NoneVal()));
        var asm = Compile(prog);
        Assert.Contains("la\tt0, __flash_PATTERN", asm);
        Assert.Contains("lbu\tt1, 0(t0)", asm);
        Assert.DoesNotContain("lpm", asm);
    }

    [Fact]
    public void FlashStringAddressResolvesToItsTableSymbol()
    {
        var prog = MakeProgram("main",
            new Copy(new FlashStrAddr("msg"), new Variable("p")),
            new Return(new NoneVal()));
        Assert.Contains("la\tt0, __flash_msg", Compile(prog));
    }

    [Fact]
    public void BytearrayAccessGoesThroughThePointerSlot()
    {
        var prog = MakeProgram("main",
            new BytearrayLoad("buf", new Variable("i"), new Variable("v")),
            new BytearrayStore("buf", new Variable("i"), new Constant(7)),
            new Return(new NoneVal()));
        var asm = Compile(prog);
        Assert.Contains("lbu\tt1, 0(t0)", asm);
        Assert.Contains("sb\tt1, 0(t0)", asm);
    }

    // ─── Interrupts ──────────────────────────────────────────────────────────

    private static ProgramIR MakeIsrProgram(int vector, string name = "on_tick")
    {
        var prog = MakeProgram("main", new Return(new NoneVal()));
        prog.Functions.Add(new Function
        {
            Name = name,
            IsInterrupt = true,
            InterruptVector = vector,
            Body = [new Return(new NoneVal())],
        });
        return prog;
    }

    [Fact]
    public void IsrPreservesCallerSavedRegisters()
    {
        // The interrupted code holds live values in these; an ISR that clobbers
        // them corrupts whatever it interrupted.
        var asm = Compile(MakeIsrProgram(12));
        foreach (var reg in new[] { "t0", "t1", "t2", "a0", "a1", "a2", "a3", "a4", "a5", "s1" })
        {
            Assert.Contains($"sw\t{reg},", asm);
            Assert.Contains($"lw\t{reg},", asm);
        }
    }

    [Fact]
    public void IsrReturnsWithMret()
    {
        var asm = Compile(MakeIsrProgram(12));
        Assert.Contains("mret", asm);
    }

    [Fact]
    public void OrdinaryFunctionSavesNoInterruptContext()
    {
        var prog = MakeProgram("helper", new Return(new Constant(0)));
        var asm = Compile(prog);
        Assert.DoesNotContain("sw\ta3,", asm);
        Assert.Contains("\tret", asm);
    }

    [Fact]
    public void IsrIsWiredIntoItsVectorSlot()
    {
        var asm = Compile(MakeIsrProgram(12));
        var lines = asm.Split('\n');

        int baseIndex = Array.FindIndex(lines, l => l.StartsWith("_vector_base:"));
        Assert.True(baseIndex >= 0, "no vector table emitted");

        // Slot 0 is the reset jump, so the Nth .word line is vector N.
        var words = lines.Skip(baseIndex)
                         .Where(l => l.Contains(".word\t"))
                         .ToList();
        Assert.Equal("\t.word\ton_tick", words[11]);   // vector 12
        Assert.Equal("\t.word\t_default_handler", words[10]);
    }

    [Fact]
    public void VectorTableGrowsToFitHighVectors()
    {
        // The CH32V003 has external IRQs past the 32-slot default table.
        var asm = Compile(MakeIsrProgram(37));
        var words = asm.Split('\n').Where(l => l.Contains(".word\t")).ToList();
        Assert.Equal(37, words.Count);              // vectors 1..37
        Assert.Equal("\t.word\ton_tick", words[36]);
    }

    [Fact]
    public void IsrWithoutAVectorIsRejected()
    {
        // Vector 0 is the reset entry on QingKe; an ISR there would overwrite it.
        var ex = Assert.Throws<NotSupportedException>(() => Compile(MakeIsrProgram(0)));
        Assert.Contains("reset entry", ex.Message);
    }

    [Fact]
    public void TwoIsrsOnTheSameVectorAreRejected()
    {
        var prog = MakeIsrProgram(12);
        prog.Functions.Add(new Function
        {
            Name = "other",
            IsInterrupt = true,
            InterruptVector = 12,
            Body = [new Return(new NoneVal())],
        });

        var ex = Assert.Throws<NotSupportedException>(() => Compile(prog));
        Assert.Contains("claimed by both", ex.Message);
    }

    // ─── Access widths ───────────────────────────────────────────────────────

    [Theory]
    [InlineData(DataType.UINT8, "lbu")]
    [InlineData(DataType.INT8, "lb")]
    [InlineData(DataType.UINT16, "lhu")]
    [InlineData(DataType.INT16, "lh")]
    [InlineData(DataType.UINT32, "lw")]
    [InlineData(DataType.INT32, "lw")]
    public void LoadIndirectUsesTheElementWidth(DataType elem, string mnemonic)
    {
        // Signed narrow loads must sign-extend, unsigned ones zero-extend.
        var prog = MakeProgram("main",
            new LoadIndirect(new Variable("p"), new Variable("v"), elem),
            new Return(new NoneVal()));
        Assert.Contains($"{mnemonic}\tt1, 0(t0)", Compile(prog));
    }

    [Theory]
    [InlineData(DataType.UINT8, "sb", "lbu")]
    [InlineData(DataType.UINT16, "sh", "lhu")]
    [InlineData(DataType.INT16, "sh", "lh")]
    [InlineData(DataType.UINT32, "sw", "lw")]
    [InlineData(DataType.INT32, "sw", "lw")]
    public void MmioAccessUsesTheRegisterWidth(DataType type, string store, string load)
    {
        // A word store into an 8-bit peripheral register clobbers the three
        // registers next to it.
        var reg = new MemoryAddress(0x40011000, type);
        var prog = MakeProgram("main",
            new Copy(new Constant(0xA5), reg),
            new Copy(reg, new Variable("v")),
            new Return(new NoneVal()));
        var asm = Compile(prog);

        Assert.Contains($"{store}\tt0, 0(t2)", asm);
        Assert.Contains($"{load}\tt0, 0(t2)", asm);
    }

    [Theory]
    [InlineData(DataType.UINT8, "sb")]
    [InlineData(DataType.INT8, "sb")]
    [InlineData(DataType.UINT16, "sh")]
    [InlineData(DataType.INT16, "sh")]
    [InlineData(DataType.UINT32, "sw")]
    [InlineData(DataType.INT32, "sw")]
    public void StoreIndirectUsesTheElementWidth(DataType elem, string mnemonic)
    {
        // Writing a whole word through an 8-bit pointer would clobber the three
        // neighbouring registers of a peripheral block.
        var prog = MakeProgram("main",
            new StoreIndirect(new Variable("v"), new Variable("p"), elem),
            new Return(new NoneVal()));
        Assert.Contains($"{mnemonic}\tt1, 0(t0)", Compile(prog));
    }

    // ─── Error propagation ───────────────────────────────────────────────────

    [Fact]
    public void SignalSuccessClearsTheErrorRegister()
    {
        var prog = MakeProgram("helper", new SignalSuccess(), new Return(new Constant(0)));
        Assert.Contains("li\ts1, 0", Compile(prog));
    }

    [Fact]
    public void UncaughtSignalErrorReturnsWithTheErrorCode()
    {
        var prog = MakeProgram("helper",
            new SignalError(new Constant(6)),
            new Return(new Constant(0)));
        var asm = Compile(prog);
        Assert.Contains("li\ts1, 6", asm);
        // It must tear the frame down and return, not fall through.
        Assert.Contains("lw\ts0,", asm);
        Assert.Contains("ret", asm);
    }

    [Fact]
    public void CaughtSignalErrorJumpsToTheHandler()
    {
        var prog = MakeProgram("helper",
            new SignalError(new Constant(6), "catch_0"),
            new Label("catch_0"),
            new Return(new Constant(0)));
        var asm = Compile(prog);
        Assert.Contains("li\ts1, 6", asm);
        Assert.Contains("j\tcatch_0", asm);
    }

    [Fact]
    public void BranchOnErrorTestsTheErrorRegister()
    {
        var prog = MakeProgram("main",
            new Call("may_fail", [], new Variable("r")),
            new BranchOnError("on_error"),
            new Label("on_error"),
            new Return(new NoneVal()));
        Assert.Contains("bnez\ts1, on_error", Compile(prog));
    }

    // ─── Unsupported IR ──────────────────────────────────────────────────────

    [Fact]
    public void UnsupportedInstructionFailsLoudly()
    {
        // Silently skipping an instruction would produce plausible-looking but
        // wrong firmware, so unimplemented IR must stop the build.
        var prog = MakeProgram("main", new GcAlloc(new Constant(8), new Variable("p")));
        var ex = Assert.Throws<NotSupportedException>(() => Compile(prog));
        Assert.Contains("GcAlloc", ex.Message);
    }

    // ─── Assembler round-trip ────────────────────────────────────────────────

    [Fact]
    public void OutputAssemblesWithGnuAs()
    {
        var assembler = FindAssembler();
        if (assembler is null) return;   // toolchain not installed on this machine

        var prog = MakeProgram("main",
            new Copy(new Constant(1), new Variable("counter")),
            new Call("helper", [new Variable("counter")], new Variable("r")),
            new Binary(BinaryOp.Mul, new Variable("r"), new Constant(3), new Variable("r")),
            new Binary(BinaryOp.Div, new Variable("r"), new Constant(2), new Variable("r")),
            new Binary(BinaryOp.Mod, new Variable("r"), new Constant(5), new Variable("r")),
            new BitSet(new MemoryAddress(0x40011408), 4),
            new JumpIfLessThan(new Variable("r"), new Constant(10), "spin"),
            new Label("spin"),
            new Return(new NoneVal()));
        prog.Globals.Add(new Variable("counter"));
        prog.Functions.Add(new Function
        {
            Name = "helper",
            Params = ["x"],
            Body = [new Return(new Variable("x"))],
        });

        var dir = Directory.CreateTempSubdirectory("pymcu-riscv-");
        try
        {
            var asmPath = Path.Combine(dir.FullName, "firmware.s");
            File.WriteAllText(asmPath, Compile(prog));

            var psi = new ProcessStartInfo(assembler)
            {
                RedirectStandardError = true,
                WorkingDirectory = dir.FullName,
            };
            psi.ArgumentList.Add("-mabi=ilp32e");
            psi.ArgumentList.Add(asmPath);
            psi.ArgumentList.Add("-o");
            psi.ArgumentList.Add(Path.Combine(dir.FullName, "firmware.o"));

            using var proc = Process.Start(psi)!;
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();

            Assert.True(proc.ExitCode == 0, $"riscv32-unknown-elf-as rejected the output:\n{stderr}");
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    private static string? FindAssembler()
    {
        var paths = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator);
        foreach (var dir in paths)
        {
            if (string.IsNullOrEmpty(dir)) continue;
            var candidate = Path.Combine(dir, "riscv32-unknown-elf-as");
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }
}
