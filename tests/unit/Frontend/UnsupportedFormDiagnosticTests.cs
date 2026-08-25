using PyMCU.Common;
using PyMCU.Frontend;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// Some unsupported Python forms are rejected with a message naming the construct and the way
/// out; others used to report where the parser stopped, which names nothing. These pin the ones
/// that were still in the second column: the reader has to be able to tell a typo from a
/// feature that does not exist.
///
/// `**=` is here for the opposite reason. It was in the same column ("Expected expression"),
/// but the binary `**` already compiled, so the spelling was all that was missing and the
/// answer was to accept it rather than to explain it away.
/// </summary>
public class UnsupportedFormDiagnosticTests
{
    private static string ErrorFor(string src)
        => Assert.ThrowsAny<Exception>(() => new Parser(new Lexer(src).Tokenize()).ParseProgram()).Message;

    [Fact]
    public void AsyncWith_NamesTheConstruct()
    {
        var msg = ErrorFor("async def main():\n    async with lock:\n        pass\n");

        Assert.Contains("'async with'", msg);
        Assert.DoesNotContain("Expected newline or end of block", msg);
    }

    [Fact]
    public void AsyncWith_SaysWhatIsMissing_AndWhatToDoInstead()
    {
        var msg = ErrorFor("async def main():\n    async with lock:\n        pass\n");

        Assert.Contains("coroutine", msg);
        Assert.Contains("'with'", msg);
    }

    [Fact]
    public void AsyncFor_NamesTheConstruct_AndPointsAtThePlainForm()
    {
        var msg = ErrorFor("async def main():\n    async for x in q:\n        pass\n");

        Assert.Contains("'async for'", msg);
        Assert.Contains("'for'", msg);
        Assert.DoesNotContain("Expected newline or end of block", msg);
    }

    [Fact]
    public void KwargsParameter_NamesIt_AndSaysToDeclareTheKeywordsExplicitly()
    {
        var msg = ErrorFor("def f(**kwargs):\n    pass\n");

        Assert.Contains("'**kwargs'", msg);
        Assert.Contains("explicitly", msg);
        Assert.DoesNotContain("Expected parameter name", msg);
    }

    [Fact]
    public void Del_NamesIt_AndSaysWhyStaticStorageHasNothingToUnbind()
    {
        var msg = ErrorFor("def main():\n    a = 1\n    del a\n");

        Assert.Contains("'del'", msg);
        Assert.Contains("static", msg);
        Assert.DoesNotContain("Expected newline or end of block", msg);
    }

    [Fact]
    public void DelOfASubscript_GetsTheSameNamedRejection()
    {
        var msg = ErrorFor("def main():\n    d: uint8[2] = [1, 2]\n    del d[0]\n");

        Assert.Contains("'del'", msg);
        Assert.DoesNotContain("Expected newline or end of block", msg);
    }

    [Fact]
    public void ATupleTargetInAComprehension_SaysWhereTheSameTargetDoesWork()
    {
        // "Expected 'in'" pointed at a word that is right there, one token away, while the
        // identical target is accepted by a plain `for` statement.
        var msg = ErrorFor("def main():\n"
                           + "    base: uint8[4] = [10, 20, 30, 40]\n"
                           + "    out = [i + v for i, v in enumerate(base)]\n");

        Assert.Contains("comprehension", msg);
        Assert.Contains("`for` statement", msg);
        Assert.DoesNotContain("Expected 'in'", msg);
    }

    [Fact]
    public void ANestedUnpackingTarget_NamesTheShape_AndHowToWriteItFlat()
    {
        var msg = ErrorFor("def main():\n    s: uint8 = 1\n    (a, b), c = (s, 1), 2\n");

        Assert.Contains("nested unpacking target", msg);
        Assert.Contains("two steps", msg);
        Assert.DoesNotContain("Expected newline or end of block", msg);
    }

    [Fact]
    public void PowAssign_Compiles()
    {
        // The binary `**` already worked, so the spelling was what was missing, not the
        // operator; "Expected expression" conveyed neither.
        var p = new Parser(new Lexer("def main():\n    a: int16 = 3\n    a **= 2\n").Tokenize())
            .ParseProgram();

        Assert.Single(p.Functions);
    }

    [Fact]
    public void PowAssign_IsTheSameProgramAsTheSpellingThatAlreadyWorked()
    {
        var augmented = Ir("def main():\n    a: int16 = 3\n    a **= 2\n");
        var written = Ir("def main():\n    a: int16 = 3\n    a = a ** 2\n");

        Assert.Equal(written, augmented);
    }

    /// <summary>The instruction stream of a program, as text, for comparing two spellings.</summary>
    private static string Ir(string src)
    {
        var ast = new Parser(new Lexer(src).Tokenize()).ParseProgram();
        var ir = new PyMCU.IR.IRGenerator.IRGenerator().Generate(
            ast, new Dictionary<string, ProgramNode>(),
            new PyMCU.Common.Models.DeviceConfig { Arch = "avr" });

        return string.Join("\n", ir.Functions
            .Single(f => f.Name == "main").Body
            .Where(i => i is not PyMCU.IR.DebugLine)
            .Select(i => i.ToString()));
    }
}
