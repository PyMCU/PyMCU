using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using BinaryOp = PyMCU.IR.BinaryOp;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// Integer add/sub/mul promotes to the next wider type, but uint32 has none, so a uint32
/// result that could be negative stayed uint32 and the range fold then read "a uint32 is
/// never negative": `(s - 300) == (30000 // (u + 7)) + -4585` folded to False with no code
/// emitted (CPython: True). The result takes int32 when it fits or when an operand brings
/// a sign; unsigned operands keep uint32 and its documented wrap; and a uint32 compared
/// with something that can be negative is left to the emitted comparison.
/// </summary>
public class Uint32SignRangeTests
{
    private const string Regs =
        "from pymcu.types import uint8, uint16, int16, uint32, int32, ptr\n" +
        "G: ptr[uint8] = ptr(0x3E)\n";

    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(Regs + src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });

    private static List<Instruction> Main(ProgramIR ir) =>
        ir.Functions.Single(f => f.Name == "main").Body;

    [Fact]
    public void ABoundedQuotientPlusANegative_IsSignedAndTheComparisonStays()
    {
        var body = Main(Gen(
            "u: uint16 = G.value\n" +
            "r = (G.value - 300) == (30000 // (u + 7)) + -4585\n"));

        var add = Assert.Single(body.OfType<Binary>(),
            b => b.Op == BinaryOp.Add && b.Src2 is Constant { Value: -4585 });
        Assert.Equal(DataType.INT32, ((Temporary)add.Dst).Type);
        Assert.Contains(body, i => i is Binary { Op: BinaryOp.Equal });
    }

    [Fact]
    public void ASignedOperandTimesAUint32_IsSigned()
    {
        var body = Main(Gen(
            "a: int16 = G.value - 300\n" +
            "b: uint32 = G.value + 80000\n" +
            "r = a * b\n"));

        var mul = Assert.Single(body.OfType<Binary>(), b => b.Op == BinaryOp.Mul);
        Assert.Equal(DataType.INT32, ((Temporary)mul.Dst).Type);
    }

    // The control: two unsigned operands keep uint32. `a - 5` with a near 3e9 is positive,
    // and int32 would print it negative.
    [Fact]
    public void AUint32MinusAPositive_StaysUnsigned()
    {
        var body = Main(Gen(
            "a: uint32 = G.value + 3000000000\n" +
            "r = a - 5\n"));

        var sub = Assert.Single(body.OfType<Binary>(), b => b.Op == BinaryOp.Sub);
        Assert.Equal(DataType.UINT32, ((Temporary)sub.Dst).Type);
    }

    [Fact]
    public void AUint32AgainstANegative_IsNotFolded()
    {
        var body = Main(Gen(
            "a: uint32 = G.value + 70000\n" +
            "b: uint32 = G.value + 80000\n" +
            "r = (a - b) == -10000\n"));

        Assert.Contains(body, i => i is Binary { Op: BinaryOp.Equal });
    }
}
