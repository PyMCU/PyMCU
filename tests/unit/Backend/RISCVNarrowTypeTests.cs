using PyMCU.Backend.Targets.RiscV;
using PyMCU.Common.Models;
using PyMCU.IR;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// Registers and frame slots on RV32E are whole words. A result has to be wrapped to the width
/// its destination declares, a signed value has to shift arithmetically, and a uint32 or a
/// pointer has to compare as unsigned, or the 32-bit machine leaks into narrow-typed code.
/// </summary>
public class RISCVNarrowTypeTests
{
    private static readonly DeviceConfig Ch32v003 = new() { Chip = "ch32v003", Arch = "ch32v" };

    private static string Compile(params Instruction[] body)
    {
        var p = new ProgramIR();
        p.Functions.Add(new Function { Name = "main", Body = body.ToList() });
        var sw = new StringWriter();
        new RiscvCodeGen(Ch32v003).Compile(p, sw);
        return sw.ToString();
    }

    private static Variable V(string name, DataType t) => new(name, t);

    [Fact]
    public void AUint8ResultIsMaskedToEightBits()
    {
        var asm = Compile(new Binary(BinaryOp.Add, V("a", DataType.UINT8), new Constant(1), V("r", DataType.UINT8)));

        Assert.Matches(@"(?s)addi\tt0, t0, 1\s+andi\tt0, t0, 255\s+sw\tt0", asm);
    }

    [Fact]
    public void AnInt8ResultIsSignExtendedFromBit7()
    {
        var asm = Compile(new Binary(BinaryOp.Add, V("a", DataType.INT8), new Constant(1), V("r", DataType.INT8)));

        Assert.Matches(@"(?s)slli\tt0, t0, 24\s+srai\tt0, t0, 24\s+sw\tt0", asm);
    }

    [Fact]
    public void ASixteenBitResultIsWrappedToSixteenBits()
    {
        var u = Compile(new Binary(BinaryOp.Add, V("a", DataType.UINT16), new Constant(1), V("r", DataType.UINT16)));
        var s = Compile(new Binary(BinaryOp.Add, V("a", DataType.INT16), new Constant(1), V("r", DataType.INT16)));

        Assert.Matches(@"(?s)slli\tt0, t0, 16\s+srli\tt0, t0, 16\s+sw\tt0", u);
        Assert.Matches(@"(?s)slli\tt0, t0, 16\s+srai\tt0, t0, 16\s+sw\tt0", s);
    }

    [Fact]
    public void AThirtyTwoBitResultIsStoredAsIs()
    {
        var asm = Compile(new Binary(BinaryOp.Add, V("a", DataType.UINT32), new Constant(1), V("r", DataType.UINT32)));

        Assert.DoesNotContain("andi\tt0, t0, 255", asm);
        Assert.DoesNotContain("slli\tt0, t0, 16", asm);
    }

    [Fact]
    public void ARightShiftOfASignedValueIsArithmetic()
    {
        var asm = Compile(new Binary(BinaryOp.RShift, V("a", DataType.INT8), V("n", DataType.UINT8), V("r", DataType.INT8)));

        Assert.Contains("sra\tt0, t0, t1", asm);
        Assert.DoesNotContain("srl\tt0, t0, t1", asm);
    }

    [Fact]
    public void ARightShiftOfAnUnsignedValueIsLogical()
    {
        var asm = Compile(new Binary(BinaryOp.RShift, V("a", DataType.UINT32), V("n", DataType.UINT8), V("r", DataType.UINT32)));

        Assert.Contains("srl\tt0, t0, t1", asm);
    }

    [Fact]
    public void AUint32BranchComparesAsUnsigned()
    {
        var asm = Compile(
            new JumpIfLessThan(V("a", DataType.UINT32), V("b", DataType.UINT32), "L_1"),
            new Label("L_1"));

        Assert.Contains("bltu\tt0, t1, L_1", asm);
    }

    [Fact]
    public void AnInt32BranchComparesAsSigned()
    {
        var asm = Compile(
            new JumpIfLessThan(V("a", DataType.INT32), V("b", DataType.INT32), "L_1"),
            new Label("L_1"));

        Assert.Contains("blt\tt0, t1, L_1", asm);
    }

    [Fact]
    public void AUint32ComparisonValueIsSetWithSltu()
    {
        var asm = Compile(new Binary(BinaryOp.LessThan, V("a", DataType.UINT32), V("b", DataType.UINT32), V("r", DataType.UINT8)));

        Assert.Contains("sltu\tt0, t0, t1", asm);
    }

    [Fact]
    public void ANarrowUnsignedBranchStaysSigned()
    {
        var asm = Compile(
            new JumpIfLessThan(V("a", DataType.UINT8), V("b", DataType.UINT8), "L_1"),
            new Label("L_1"));

        Assert.Contains("blt\tt0, t1, L_1", asm);
    }
}
