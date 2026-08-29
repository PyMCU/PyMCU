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

    /// <summary>
    /// The constant a function returns, once folding and narrowing have both run. The
    /// narrowing to the declared width happens in the optimizer, not in the IR generator,
    /// so a raw Gen() would see int8 -128 // -1 as the un-narrowed 128.
    /// </summary>
    private static int Returned(string src)
    {
        var ir = Optimizer.Optimize(Gen(src));
        foreach (var f in ir.Functions)
            foreach (var ins in f.Body)
                if (ins is Return { Value: Constant c }) return c.Value;
        throw new Xunit.Sdk.XunitException("no folded constant was returned; it stayed a run-time expression");
    }

    // ─── #223: MIN // -1 folds to what the chip computes ───────────────────
    //
    // The oracle here is not a reading of Python's semantics but the compiler's own run-time
    // answer: with operands the folder cannot see through, an atmega328p produces -2147483648
    // for int32 MIN // -1 and 0 for MIN % -1. int8 and int16 already folded to their own wrap.
    // Only int32 threw, because its true quotient 2147483648 does not fit the int the fold was
    // computed in, and C# raises OverflowException on int.MinValue / -1.

    [Fact]
    public void TheInt32Floor_DividedByMinusOne_FoldsToWhatTheChipComputes()
    {
        Assert.Equal(-2147483648, Returned(
            "def main() -> int32:\n" +
            "    q: int32 = -2147483648 // -1\n" +
            "    return q\n"));
    }

    [Fact]
    public void TheInt32Floor_ModuloMinusOne_FoldsToZero()
    {
        Assert.Equal(0, Returned(
            "def main() -> int32:\n" +
            "    r: int32 = -2147483648 % -1\n" +
            "    return r\n"));
    }

    [Fact]
    public void TheSameThroughPropagatedVariables_FoldsInTheOptimizerToo()
    {
        // A second fold site, reached only once constant propagation has replaced the
        // variables with their values. Fixing the IR generator alone left this one throwing,
        // so the reproducer in the issue would have passed while the defect stayed.
        Assert.Equal(-2147483648, Returned(
            "def main() -> int32:\n" +
            "    a: int32 = -2147483648\n" +
            "    b: int32 = -1\n" +
            "    q: int32 = a // b\n" +
            "    return q\n"));
        Assert.Equal(0, Returned(
            "def main() -> int32:\n" +
            "    a: int32 = -2147483648\n" +
            "    b: int32 = -1\n" +
            "    r: int32 = a % b\n" +
            "    return r\n"));
    }

    [Fact]
    public void TheNarrowerFloors_FoldToTheirOwnWrap()
    {
        // These never threw, and they are why the int32 answer is a wrap rather than a
        // diagnostic: each folds to exactly what the same expression executes.
        Assert.Equal(-128, Returned(
            "def main() -> int8:\n    q: int8 = -128 // -1\n    return q\n"));
        Assert.Equal(0, Returned(
            "def main() -> int8:\n    r: int8 = -128 % -1\n    return r\n"));
        Assert.Equal(-32768, Returned(
            "def main() -> int16:\n    q: int16 = -32768 // -1\n    return q\n"));
        Assert.Equal(0, Returned(
            "def main() -> int16:\n    r: int16 = -32768 % -1\n    return r\n"));
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
