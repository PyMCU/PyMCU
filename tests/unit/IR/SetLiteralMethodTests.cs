using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// A method call on a set literal (issue #197).
///
/// `s.add(3)` was reported as "call to undefined function 's_add' (typo, or a missing
/// import?)". Three things wrong in one line: `s_add` appears nowhere in the program, `.add`
/// is a method and not a function, and neither suggestion applies since the spelling is right
/// and no import can add a method to a set literal.
///
/// The dict path answers the same question properly, so these also pin that the two read
/// alike rather than only that the set one stopped being wrong.
/// </summary>
public class SetLiteralMethodTests
{
    private static PyMCU.Common.CompilerError Reject(string src)
        => Assert.ThrowsAny<PyMCU.Common.CompilerError>(() =>
               new IRGenerator().Generate(
                   new Parser(new Lexer(src).Tokenize()).ParseProgram(),
                   new Dictionary<string, ProgramNode>(),
                   new DeviceConfig { Arch = "avr" }));

    [Fact]
    public void ItNamesTheConstruct_NotAManufacturedSymbol()
    {
        var ex = Reject("def main():\n    s = {1, 2}\n    s.add(3)\n");

        Assert.Contains("'s' is a compile-time set literal", ex.Message);
        Assert.Contains("'add()'", ex.Message);
        Assert.DoesNotContain("s_add", ex.Message);
        Assert.DoesNotContain("undefined function", ex.Message);
        Assert.DoesNotContain("missing import", ex.Message);
    }

    [Fact]
    public void ItStatesWhatIsSupported_AndAnAlternative()
    {
        var ex = Reject("def main():\n    s = {1, 2}\n    s.discard(1)\n");

        Assert.Contains("x in s", ex.Message);
        Assert.Contains("len(s)", ex.Message);
        Assert.Contains("bytearray", ex.Message);
    }

    // Every member, not a list of rejected ones: nothing on a set literal has a lowering.
    [Theory]
    [InlineData("add")]
    [InlineData("remove")]
    [InlineData("pop")]
    [InlineData("union")]
    [InlineData("clear")]
    public void EveryMethodOnASetLiteral_IsRefusedTheSameWay(string member)
    {
        var ex = Reject($"def main():\n    s = {{1, 2}}\n    s.{member}(1)\n");

        Assert.Contains("is a compile-time set literal", ex.Message);
        Assert.Contains($"'{member}()'", ex.Message);
    }

    // The sibling it is modelled on, so a later edit to one shows up as a divergence. Both
    // name the receiver: two messages answering the same question differently about whether
    // they tell you WHICH collection is the problem is the inconsistency each exists to fix,
    // and it matters as soon as two sets are in scope.
    [Fact]
    public void TheDictSibling_NamesItsReceiverToo()
    {
        var ex = Reject("def main():\n    d = {1: 2}\n    d.pop(1)\n");

        Assert.Contains("'d' is a compile-time lookup table", ex.Message);
        Assert.Contains("FixedDict", ex.Message);
    }

    // A real method call must not be caught by the set path.
    [Fact]
    public void AMethodOnAnInstance_IsUnaffected()
    {
        var ir = new IRGenerator().Generate(
            new Parser(new Lexer(
                "class Counter:\n" +
                "    def __init__(self, n: uint8):\n" +
                "        self.n = n\n" +
                "\n" +
                "    def bump(self) -> uint8:\n" +
                "        return self.n + 1\n" +
                "\n" +
                "def main():\n" +
                "    c = Counter(4)\n" +
                "    r: uint8 = c.bump()\n").Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });

        Assert.Contains(ir.Functions, f => f.Name == "main");
    }
}
