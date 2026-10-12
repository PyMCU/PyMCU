using System.Text.RegularExpressions;
using PyMCU.Backend.Targets.RiscV;
using PyMCU.Common.Models;
using PyMCU.IR;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// The CH32V003 has no FPU: a float is its binary32 bits in an integer register and every
/// operation is a call into routines the backend emits (compiled from src/softfloat/softfloat.c,
/// whose results are checked against the host's hardware floats bit for bit by
/// tests/riscv_softfloat).
/// </summary>
public class RISCVSoftFloatTests
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

    private static Variable F(string n) => new(n, DataType.FLOAT);
    private static Variable U8(string n) => new(n, DataType.UINT8);

    [Fact]
    public void AFloatConstantIsLoadedAsItsBinary32Bits()
    {
        var asm = Compile(new Copy(new FloatConstant(1.0), F("x")));

        Assert.Contains("li\tt0, 1065353216", asm);       // 0x3F800000
    }

    [Fact]
    public void AConstantRoundsToBinary32WhenItIsLoaded()
    {
        var asm = Compile(new Copy(new FloatConstant(0.1), F("x")));

        Assert.Contains("li\tt0, 1036831949", asm);       // 0x3DCCCCCD
    }

    [Fact]
    public void AnAdditionCallsTheSoftFloatRoutineAndEmitsItWithWhatItCalls()
    {
        var asm = Compile(new Binary(BinaryOp.Add, F("a"), F("b"), F("r")));

        Assert.Contains("call\t__pymcu_fadd", asm);
        Assert.Matches(@"(?m)^__pymcu_fadd:", asm);
        Assert.Matches(@"(?m)^__pymcu_sf_addsub:", asm);
        Assert.Matches(@"(?m)^__pymcu_sf_pack:", asm);
        Assert.Matches(@"(?m)^__pymcu_sf_jam:", asm);
    }

    [Fact]
    public void ARoutineNothingCallsIsNotEmitted()
    {
        var asm = Compile(new Binary(BinaryOp.Add, F("a"), F("b"), F("r")));

        Assert.DoesNotMatch(@"(?m)^__pymcu_fdiv:", asm);
        Assert.DoesNotMatch(@"(?m)^__pymcu_fcmp:", asm);
    }

    [Fact]
    public void AProgramWithoutFloatsEmitsNoSoftFloat()
    {
        var asm = Compile(new Binary(BinaryOp.Add, U8("a"), U8("b"), U8("r")));

        Assert.DoesNotContain("__pymcu_f", asm);
        Assert.DoesNotContain("__pymcu_sf_", asm);
    }

    [Fact]
    public void AMultiplicationPullsInTheIntegerMultiplyHelperItCallsEvenWhereTheCoreHasMulDiv()
    {
        var asm = Compile(new Binary(BinaryOp.Mul, F("a"), F("b"), F("r")));

        Assert.Contains("call\t__pymcu_fmul", asm);
        Assert.Matches(@"(?m)^__mulsi3:", asm);
    }

    [Fact]
    public void AComparisonCallsTheCompareRoutineAndReducesItsAnswer()
    {
        var asm = Compile(new Binary(BinaryOp.LessThan, F("a"), F("b"), U8("r")));

        Assert.Contains("call\t__pymcu_fcmp", asm);
        Assert.Matches(@"addi\tt0, a0, 1\s+seqz\tt0, t0", asm);    // -1 means less
    }

    [Fact]
    public void ABranchOnAFloatRelationCallsTheCompareRoutine()
    {
        var asm = Compile(new JumpIfGreaterThan(F("a"), F("b"), "L_1"), new Label("L_1"));

        Assert.Contains("call\t__pymcu_fcmp", asm);
        Assert.Contains("bnez\tt0, L_1", asm);
    }

    [Fact]
    public void AnIntegerOperandIsConvertedBeforeTheOperation()
    {
        var asm = Compile(new Binary(BinaryOp.Add, F("a"), U8("n"), F("r")));

        Assert.Matches(@"call\t__pymcu_i2f[\s\S]*call\t__pymcu_fadd", asm);
    }

    [Fact]
    public void AnIntegerConstantIsConvertedByTheCompiler()
    {
        var asm = Compile(new Binary(BinaryOp.Add, F("a"), new Constant(2), F("r")));

        Assert.Contains("li\ta1, 1073741824", asm);        // 2.0f
        Assert.DoesNotContain("call\t__pymcu_i2f", asm);
    }

    [Fact]
    public void CopyingAnIntegerIntoAFloatConverts()
    {
        var asm = Compile(new Copy(U8("n"), F("x")));

        Assert.Contains("call\t__pymcu_i2f", asm);
    }

    [Fact]
    public void CopyingAFloatIntoAnIntegerTruncatesTowardZero()
    {
        var asm = Compile(new Copy(F("x"), new Variable("n", DataType.INT16)));

        Assert.Contains("call\t__pymcu_f2i", asm);
    }

    [Fact]
    public void ABitcastKeepsTheBits()
    {
        var asm = Compile(new Bitcast(F("x"), new Variable("n", DataType.UINT32)));

        Assert.DoesNotContain("__pymcu_", asm);
    }

    [Fact]
    public void NegatingAFloatFlipsTheSignBit()
    {
        var asm = Compile(new Unary(UnaryOp.Neg, F("x"), F("r")));

        Assert.Matches(@"li\tt1, -2147483648\s+xor\tt0, t0, t1", asm);
    }

    [Fact]
    public void AFunctionThatDoesFloatMathSavesTheReturnAddress()
    {
        var asm = Compile(new Binary(BinaryOp.Add, F("a"), F("b"), F("r")));
        var main = asm[asm.IndexOf("\nmain:", StringComparison.Ordinal)..];

        Assert.Contains("sw\tra,", main);
    }

    [Fact]
    public void TheSoftFloatRoutinesLeaveTheErrorRegisterAlone()
    {
        var asm = Compile(
            new Binary(BinaryOp.Add, F("a"), F("b"), F("r")),
            new Binary(BinaryOp.Mul, F("a"), F("b"), F("r")),
            new Binary(BinaryOp.Div, F("a"), F("b"), F("r")),
            new Binary(BinaryOp.LessThan, F("a"), F("b"), U8("c")));

        var routines = asm[asm.IndexOf("__pymcu_sf_", StringComparison.Ordinal)..];
        Assert.DoesNotMatch(new Regex(@"\bs1\b"), routines.Replace("\ts1", "\ts1_"));
    }
}
