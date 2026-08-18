using PyMCU.Common.Models;
using PyMCU.Frontend;
using Xunit;

namespace PyMCU.UnitTests;

public class CompileTimeSizeTests
{
    private static CompileTimeEvaluator Eval(int flash = 4096, int ram = 224, ulong freq = 4_000_000)
        => new(new DeviceConfig
        {
            Chip = "pic16f628a", Arch = "pic14",
            FlashSize = flash, RamSize = ram, Frequency = freq,
        });

    private static Expression Chip(string member)
        => new MemberAccessExpr(new VariableExpr("__CHIP__"), member);

    private static Expression Cmp(Expression l, BinaryOp op, Expression r)
        => new BinaryExpr(l, op, r);

    [Theory]
    [InlineData("flash_size", BinaryOp.GreaterEq, 4096, true)]
    [InlineData("flash_size", BinaryOp.Greater, 4096, false)]
    [InlineData("flash_size", BinaryOp.Less, 2048, false)]
    [InlineData("flash_size", BinaryOp.LessEq, 4096, true)]
    [InlineData("ram_size", BinaryOp.GreaterEq, 224, true)]
    [InlineData("ram_size", BinaryOp.Less, 224, false)]
    public void SizeComparisons_FoldNumerically(string member, BinaryOp op, int rhs, bool expected)
        => Assert.Equal(expected,
            Eval().EvaluateCondition(Cmp(Chip(member), op, new IntegerLiteral(rhs))));

    [Fact]
    public void FrequencyComparesNumericallyToo()
        => Assert.True(Eval().EvaluateCondition(
            Cmp(new VariableExpr("__FREQ__"), BinaryOp.Greater, new IntegerLiteral(1_000_000))));

    [Fact]
    public void NumericEquality_IsNotStringEquality()
    {
        Assert.True(Eval(flash: 2048).EvaluateCondition(Cmp(Chip("flash_size"), BinaryOp.Equal, new IntegerLiteral(2048))));
        Assert.False(Eval(flash: 2048).EvaluateCondition(Cmp(Chip("flash_size"), BinaryOp.Equal, new IntegerLiteral(4096))));
    }

    [Fact]
    public void NameComparison_StillWorks()
        => Assert.True(Eval().EvaluateCondition(Cmp(Chip("name"), BinaryOp.Equal, new StringLiteral("pic16f628a"))));

    [Fact]
    public void MixingANumberWithAName_IsARefusal_NotACoercion()
    {
        var ex = Assert.Throws<PyMCU.Common.CompilerError>(
            () => Eval().EvaluateCondition(Cmp(Chip("name"), BinaryOp.Equal, new IntegerLiteral(2048))));
        Assert.Contains("no conversion between them", ex.Message);
    }

    [Fact]
    public void RelationalOnNames_SaysWhatItNeeds()
    {
        var ex = Assert.Throws<PyMCU.Common.CompilerError>(
            () => Eval().EvaluateCondition(Cmp(Chip("name"), BinaryOp.Greater, new StringLiteral("a"))));
        Assert.Contains("both sides must be numbers", ex.Message);
    }
}
