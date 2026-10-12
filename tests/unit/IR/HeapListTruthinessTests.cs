using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// The truth of a list is its length. A heap list is a name holding the address of the list, and
/// the address is never zero, so a condition that tested the name answered true for an empty
/// list: `if xs:` took the true branch, `not xs` the false one, `while xs:` never ended.
/// A condition on a heap list must read the length, and one on an unrelated integer must not.
/// </summary>
public class HeapListTruthinessTests
{
    private static List<Instruction> Body(string src)
    {
        var ir = new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr", Chip = "atmega328p" });
        return ir.Functions.SelectMany(f => f.Body).ToList();
    }

    private const string Prelude = "from pymcu.types import uint8\nxs: list[uint8] = list()\nn: uint8 = 3\n\n";

    private static bool TestsTheListName(List<Instruction> b) =>
        b.Any(i => i is JumpIfZero { Condition: Variable { Name: "xs" } }
                   or JumpIfNotZero { Condition: Variable { Name: "xs" } });

    [Theory]
    [InlineData("if xs:\n    n = 1\n")]
    [InlineData("if not xs:\n    n = 1\n")]
    [InlineData("while xs:\n    n = 1\n    break\n")]
    [InlineData("if xs and n:\n    n = 1\n")]
    [InlineData("if n or xs:\n    n = 1\n")]
    [InlineData("n = 1 if xs else 2\n")]
    [InlineData("n = 1 if not xs else 2\n")]
    [InlineData("if bool(xs):\n    n = 1\n")]
    public void AConditionOnAHeapListReadsItsLengthNotItsAddress(string use)
    {
        var b = Body(Prelude + use);
        Assert.False(TestsTheListName(b), "the list name itself was tested for zero");
    }

    [Fact]
    public void AConditionOnAnIntegerStillTestsTheInteger()
    {
        var b = Body(Prelude + "if n:\n    n = 1\n");
        Assert.Contains(b, i => i is JumpIfZero { Condition: Variable { Name: "n" } });
    }

    [Fact]
    public void AnEmptyStringIsFalse()
    {
        var b = Body("s: str = \"\"\nn: int = 0\nif s:\n    n = 1\n");
        Assert.DoesNotContain(b, i => i is Copy { Src: Constant { Value: 1 } });
    }
}
