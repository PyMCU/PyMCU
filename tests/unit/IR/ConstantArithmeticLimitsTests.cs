using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU#102 and PyMCU#107: two constant-arithmetic answers the compiler used to invent.
/// `a // 0` reached the division routine and handed back 255, and a constant that leaves
/// int32 wrapped around instead of being reported, while the same overflow one width down
/// was already a build error.
/// </summary>
public class ConstantArithmeticLimitsTests
{
    private static ProgramIR Gen(string src)
    {
        var tokens = new Lexer(src).Tokenize();
        var ast = new Parser(tokens).ParseProgram();
        return new IRGenerator().Generate(ast, new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });
    }

    [Fact]
    public void DividingARunTimeValueByLiteralZero_IsReported()
    {
        var ex = Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(
            "def main():\n" +
            "    a: uint8 = 10\n" +
            "    b: uint8 = a\n" +
            "    c: uint8 = b // 0\n"));

        Assert.Contains("division or modulo by zero", ex.Message);
    }

    [Fact]
    public void ModuloByLiteralZero_IsReportedToo()
    {
        var ex = Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(
            "def main():\n" +
            "    a: uint8 = 10\n" +
            "    b: uint8 = a\n" +
            "    c: uint8 = b % 0\n"));

        Assert.Contains("division or modulo by zero", ex.Message);
    }

    /// <summary>
    /// What matters is that the true value is named and rejected, not which of the two checks
    /// names it. This one used to reach the constant folder, because the literal check folded
    /// -2147483648 to +2147483648 and 2147483647 looked in range; since #120 the literal check
    /// computes the real -2147483649 and answers first. Both messages carry the value and the
    /// type, so the assertion is on those rather than on one wording.
    /// </summary>
    [Fact]
    public void AConstantBelowTheInt32Floor_IsReportedInsteadOfWrapping()
    {
        var ex = Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(
            "def main():\n" +
            "    x: int32 = -2147483648 - 1\n"));

        Assert.Contains("-2147483649", ex.Message);
        Assert.Contains("int32", ex.Message);
    }

    /// <summary>
    /// The ceiling was never the broken side: the literal range check catches it while it is
    /// still a literal. Pinned here so the two ends of the range keep answering.
    /// </summary>
    [Fact]
    public void AConstantAboveTheInt32Ceiling_IsReportedToo()
    {
        var ex = Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(
            "def main():\n" +
            "    x: int32 = 2147483647 + 1\n"));

        Assert.Contains("out of range for int32", ex.Message);
    }

    [Fact]
    public void TheEdgeValuesThemselves_StillCompile()
    {
        Gen("def main():\n" +
            "    hi: int32 = 2147483647\n" +
            "    sum: int32 = 2147483646 + 1\n");
    }

    /// <summary>
    /// int32's own floor can be written down. The literal used to be checked before the minus
    /// sign was applied: 2147483648 does not fit an int, so the parser stored its wrapped
    /// pattern, and negating that gave +2147483648, reported as out of range for the very type
    /// whose minimum it is (#120).
    /// </summary>
    [Fact]
    public void TheInt32Floor_CanBeWrittenDown()
    {
        Gen("def main():\n" +
            "    lo: int32 = -2147483648\n");
    }

    /// <summary>
    /// The C idiom for the same value was the workaround while #120 was open, and it has to
    /// keep working: nothing that used to build may stop building.
    /// </summary>
    [Fact]
    public void TheInt32Floor_WrittenAsASubtraction_StillCompiles()
    {
        Gen("def main():\n" +
            "    lo: int32 = -2147483647 - 1\n");
    }

    /// <summary>
    /// One past the floor is still out of range, and the message now carries the minus sign.
    /// Reading the literal's magnitude as unsigned must not swallow a real overflow.
    /// </summary>
    [Fact]
    public void OnePastTheInt32Floor_IsStillRejected()
    {
        var ex = Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(
            "def main():\n" +
            "    lo: int32 = -2147483649\n"));

        Assert.Contains("-2147483649 is out of range for int32", ex.Message);
    }

    /// <summary>
    /// A negated literal from the 2^31..2^32-1 range keeps its magnitude too, so the message
    /// names the number the program wrote rather than a wrapped one.
    /// </summary>
    [Fact]
    public void ANegatedUint32SizedLiteral_IsRejectedWithItsOwnValue()
    {
        var ex = Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(
            "def main():\n" +
            "    lo: int32 = -4294967295\n"));

        Assert.Contains("-4294967295 is out of range for int32", ex.Message);
    }
}
