using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// Two unpacking rejections used to describe something other than what the program contained:
/// a starred target on the LEFT was reported as a problem with the right-hand side, and a size
/// mismatch stated that the sizes differed without saying what either of them was.
/// </summary>
public class TupleUnpackDiagnosticTests
{
    private static ProgramIR Gen(string src)
    {
        var tokens = new Lexer(src).Tokenize();
        var ast = new Parser(tokens).ParseProgram();
        return new IRGenerator().Generate(ast, new Dictionary<string, ProgramNode>(),
                                          new DeviceConfig { Arch = "avr" });
    }

    private static string ErrorFor(string body)
        => Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen("def main():\n" + body)).Message;

    [Fact]
    public void StarredTarget_NamesTheStar_NotTheRightHandSide()
    {
        var msg = ErrorFor("    data: uint8[4] = [0, 1, 2, 3]\n    first, *rest = data\n");

        Assert.Contains("*rest", msg);
        // The right-hand side is a declared fixed-size array and is not what is unsupported.
        Assert.DoesNotContain("Tuple unpacking RHS must be", msg);
    }

    [Fact]
    public void StarredTarget_SaysWhereStarredTargetsDoWork_AndWhatToWriteInstead()
    {
        var msg = ErrorFor("    data: uint8[4] = [0, 1, 2, 3]\n    first, *rest = data\n");

        Assert.Contains("tuple literal", msg);   // the shape that IS supported
        Assert.Contains("index", msg);           // the way out
    }

    [Fact]
    public void SizeMismatch_PrintsBothSizes_AndWhichSideIsWhich()
    {
        var msg = ErrorFor("    s: uint8 = 1\n    a, b, c = s, s + 1\n");

        Assert.Contains("3 targets", msg);
        Assert.Contains("2 values", msg);
        Assert.Contains("left", msg);
        Assert.Contains("right", msg);
    }

    [Fact]
    public void SizeMismatch_NamesTheTargets()
    {
        var msg = ErrorFor("    s: uint8 = 1\n    a, b, c = s, s + 1\n");

        Assert.Contains("a, b, c", msg);
    }

    [Fact]
    public void UnpackingANamedArray_SaysThatIsWhatIsUnsupported()
    {
        var msg = ErrorFor("    data: uint8[2] = [0, 1]\n    a, b = data\n");

        Assert.Contains("tuple literal", msg);
        Assert.Contains("index", msg);
    }

    [Fact]
    public void AMatchingTupleUnpack_StillCompiles()
    {
        var ir = Gen("def main():\n    s: uint8 = 1\n    a, b = s, s + 1\n");

        Assert.Contains(ir.Functions, f => f.Name == "main");
    }
}
