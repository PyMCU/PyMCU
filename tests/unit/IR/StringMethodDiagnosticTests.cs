using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU#184. A string method on a plain literal was refused with
///
///   calling .split() on a nested member access is not yet supported
///   (a ZCA field that is itself a ZCA, like self.pin.pulse_in())
///
/// for a program with no class, no ZCA and no member access. The branch is the catch-all for
/// a receiver that is neither a name nor a register, and what actually reaches it is a method
/// on a COMPILE-TIME CONSTANT: a string, or a number literal.
///
/// The wording had also outlived its subject. `self.pin.pulse_in()`, the example it offered,
/// compiles, which the last test here pins: that is what made the sentence safe to remove
/// rather than merely reword.
/// </summary>
public class StringMethodDiagnosticTests
{
    private static ProgramIR Gen(string src)
    {
        var tokens = new Lexer(src).Tokenize();
        var ast = new Parser(tokens).ParseProgram();
        return new IRGenerator().Generate(ast, new Dictionary<string, ProgramNode>(),
                                          new DeviceConfig { Arch = "avr" });
    }

    private static string Refusal(string body)
    {
        var ex = Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(
            "def main():\n" + body + "\n"));
        return ex.Message;
    }

    // The four the issue reports, plus the two the same branch also swallows.
    [Theory]
    [InlineData("split", "    x = \"a,b,c\".split(\",\")")]
    [InlineData("upper", "    x = \"hi\".upper()")]
    [InlineData("strip", "    x = \"  hi  \".strip()")]
    [InlineData("replace", "    x = \"a-b\".replace(\"-\", \"+\")")]
    [InlineData("startswith", "    x = \"hi\".startswith(\"h\")")]
    [InlineData("find", "    x = \"hi\".find(\"i\")")]
    public void AStringMethod_IsRefusedAsAStringMethod(string method, string body)
    {
        var msg = Refusal(body);

        Assert.Contains($"'.{method}()'", msg);
        Assert.Contains("on a string", msg);
        Assert.DoesNotContain("nested member access", msg);
        Assert.DoesNotContain("ZCA", msg);
    }

    // A name bound to a literal is still a compile-time string, and reaches the same branch.
    // Assigning the receiver to a variable first is NOT a workaround, so the message must not
    // become the object-dispatch one here.
    [Fact]
    public void AStringMethodOnANameBoundToALiteral_GetsTheSameAnswer()
    {
        var msg = Refusal("    s = \"hi\"\n    x = s.upper()");

        Assert.Contains("'.upper()'", msg);
        Assert.Contains("on a string", msg);
    }

    [Fact]
    public void AMethodOnANumberLiteral_IsRefusedAsANumber()
    {
        var msg = Refusal("    x = (5).bit_length()");

        Assert.Contains("'.bit_length()'", msg);
        Assert.Contains("number literal", msg);
        Assert.DoesNotContain("ZCA", msg);
    }

    // The message names four things that work on a string. Advice that does not compile is
    // the same gap in a friendlier voice, so each one is built here.
    [Theory]
    [InlineData("    x = len(\"abc\")")]
    [InlineData("    x = \"abc\"[1]")]
    [InlineData("    n = 0\n    for c in \"abc\":\n        n = n + 1")]
    [InlineData("    s = \",\".join([\"a\", \"b\"])")]
    public void TheAdviceCompiles(string body) => Assert.NotNull(Gen("def main():\n" + body + "\n"));

    // Carved out before this branch and must stay that way: .format() lowers to an f-string,
    // and .join() as a bare expression gets its own message pointing at the assignment form.
    [Fact]
    public void FormatOnALiteral_StillLowersInsteadOfBeingRefused()
        => Assert.NotNull(Gen("def main():\n    s = \"v{}\".format(1)\n"));

    // Not the assignment form, which TryEmitJoinAssign folds, but join used as a value
    // somewhere else: that is the shape with its own message.
    [Fact]
    public void JoinUsedAsAValue_KeepsItsOwnMessage()
        => Assert.Contains("assignment form", Refusal("    x = len(\",\".join([\"a\", \"b\"]))"));

    // The reason the ZCA sentence went rather than being reworded: the program it described
    // compiles. If this ever stops compiling, the gap is real again and needs its own message
    // written against a program that reproduces it.
    [Fact]
    public void TheNestedZcaCallTheOldMessageDescribed_Compiles()
        => Assert.NotNull(Gen(
            "class Inner:\n" +
            "    def __init__(self, k: uint8):\n" +
            "        self.k = k\n" +
            "    def get(self) -> uint8:\n" +
            "        return self.k + 1\n" +
            "class Outer:\n" +
            "    def __init__(self, i: Inner):\n" +
            "        self.inner = i\n" +
            "    def run(self) -> uint8:\n" +
            "        return self.inner.get()\n" +
            "def main():\n" +
            "    o = Outer(Inner(3))\n" +
            "    v: uint8 = o.run()\n"));
}
