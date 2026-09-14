using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// The backends compare at the width of the LEFT operand. A comparison against a literal the
/// operand's type cannot hold was therefore read at that width: `count >= 404` with count:
/// uint8 tested against 148 and `count < 300` against 44; and `x < n` with x: uint8 and n:
/// uint16 read n's low byte.
///
/// Two sides whose ranges cannot overlap now fold to the one answer Python gives, and two
/// different types meet in the narrowest one that covers both, with the left side widened.
/// </summary>
public class ComparisonRangeTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    private static List<Instruction> Main(string src) => Gen(src).Functions.Single(f => f.Name == "main").Body;

    private const string Prelude = "from pymcu.types import uint8, uint16, int8\n\n";

    [Theory]
    [InlineData("count >= 404", 0)]
    [InlineData("count < 300", 1)]
    [InlineData("count == 300", 0)]
    [InlineData("count != 300", 1)]
    [InlineData("n < -200", 0)]
    public void ALiteralOutsideTheOperandsRange_Folds(string test, int expected)
    {
        var body = Main(Prelude +
            "def main(count: uint8, n: int8):\n" +
            $"    y: uint8 = 1 if {test} else 0\n\n" +
            "main(200, -5)\n");
        Assert.Contains(body, i => i is Copy { Src: Constant { Value: var v }, Dst: Variable { Name: "main.y" } } && v == expected);
        Assert.DoesNotContain(body, i => i is Binary { Op: PyMCU.IR.BinaryOp.LessThan or PyMCU.IR.BinaryOp.GreaterEqual or PyMCU.IR.BinaryOp.Equal or PyMCU.IR.BinaryOp.NotEqual });
    }

    [Fact]
    public void AnIfAgainstAnUnreachableLiteral_IsDecidedAtCompileTime()
    {
        var body = Main(Prelude +
            "def main(count: uint8):\n" +
            "    if count >= 404:\n" +
            "        y: uint8 = 1\n\n" +
            "main(200)\n");
        Assert.DoesNotContain(body, i => i is JumpIfLessThan or JumpIfGreaterOrEqual);
    }

    [Fact]
    public void ANarrowLeftSide_IsWidenedToTheRightSidesType()
    {
        var body = Main(Prelude +
            "def main(x: uint8, n: uint16):\n" +
            "    if x < n:\n" +
            "        y: uint8 = 1\n\n" +
            "main(200, 300)\n");
        var jump = body.OfType<JumpIfGreaterOrEqual>().Single();
        Assert.Equal(DataType.UINT16, ((Temporary)jump.Src1).Type);
        Assert.Contains(body, i => i is Copy { Src: Variable { Name: "main.x" }, Dst: Temporary { Type: DataType.UINT16 } });
    }

    [Fact]
    public void ALiteralOnTheLeft_IsWidenedToo()
    {
        var body = Main(Prelude +
            "def main(n: uint16):\n" +
            "    if 5 < n:\n" +
            "        y: uint8 = 1\n\n" +
            "main(300)\n");
        var jump = body.OfType<JumpIfGreaterOrEqual>().Single();
        Assert.Equal(DataType.UINT16, ((Temporary)jump.Src1).Type);
    }

    [Fact]
    public void SameTypes_StayUntouched()
    {
        var body = Main(Prelude +
            "def main(x: uint8):\n" +
            "    if x < 200:\n" +
            "        y: uint8 = 1\n\n" +
            "main(5)\n");
        var jump = body.OfType<JumpIfGreaterOrEqual>().Single();
        Assert.IsType<Variable>(jump.Src1);
        Assert.Equal(DataType.UINT8, ((Variable)jump.Src1).Type);
    }
}
