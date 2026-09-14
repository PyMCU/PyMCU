using PyMCU.Frontend;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU#352. `m[x, y]` was `Expected "]"` from the C# front end and, from the CPython bridge,
/// the generic tuple refusal -- which names the right model limit and then advises building a
/// fixed list for indexable storage, which is not what a reader indexing a matrix is doing.
///
/// It is the no-runtime-tuple limit reached through a subscript, so it is named as that, in
/// one sentence, at the first index. The CAPABILITY is a separate question and stays open.
/// </summary>
public class TwoIndexSubscriptTests
{
    private static void Parse(string src) =>
        new Parser(new Lexer(src).Tokenize()).ParseProgram();

    private static PyMCU.Common.CompilerError Refusal(string src) =>
        Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Parse(src));

    private const string Store =
        "def main():\n" +
        "    g = Grid()\n" +
        "    g[1, 2] = 3\n";

    [Fact]
    public void ATwoIndexStore_IsNamedAndNotReportedAsAMissingBracket()
    {
        var ex = Refusal(Store);
        Assert.Contains("a subscript with more than one index", ex.Message);
        Assert.DoesNotContain("Expected ']'", ex.Message);
    }

    [Fact]
    public void TheRefusalSaysWhatTheConstructDoes()
    {
        // The reader who wrote `m[x, y]` is usually not thinking about tuples at all, so the
        // sentence has to say that the pair becomes one before it can say a tuple has no
        // runtime value here.
        var ex = Refusal(Store);
        Assert.Contains("hands the pair to __getitem__ as a tuple", ex.Message);
        Assert.Contains("not a runtime value", ex.Message);
    }

    [Fact]
    public void TheRefusalOffersSomethingThatWorks()
    {
        Assert.Contains("passing the indices separately", Refusal(Store).Message);
    }

    [Fact]
    public void TheCaretIsOnTheFirstIndexAndNotOnTheComma()
    {
        // CPython stamps the Tuple at its first element. Pointing at the comma would have the
        // two front ends naming different characters for the same program.
        Assert.Equal(7, Refusal(Store).Column);
    }

    [Fact]
    public void ATwoIndexRead_GetsTheSameAnswer()
    {
        Assert.Contains("a subscript with more than one index", Refusal(
            "def main():\n" +
            "    g = Grid()\n" +
            "    y = g[1, 2]\n").Message);
    }

    [Fact]
    public void TheSubscriptFormsThatWork_AreUntouched()
    {
        Parse("def main():\n    b = bytearray(4)\n    b[0] = 1\n    c = b[1:3]\n    d = b[:2]\n    e = b[::2]\n");
        Parse("def main():\n    xs = [1, 2, 3]\n    y = xs[1]\n");
    }
}
