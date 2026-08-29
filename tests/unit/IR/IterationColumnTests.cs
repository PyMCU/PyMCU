using Xunit;
using PyMCU.Common;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;

namespace PyMCU.UnitTests;

/// <summary>
/// Where the caret lands for the diagnostics `Iteration.cs` raises (issue #177). Every one of
/// them reported the statement's line and no column, so a `for` refused for one bad element
/// pointed at the whole loop and left the reader to find which element it meant.
///
/// The two rules the issue sets are what these tests pin, and they pull in opposite
/// directions, so both halves are asserted:
///
///   * a column that IS known is reported, at the character the message is about;
///   * a column that is NOT known stays <see cref="CompilerError.Unlocated"/>. The cases at the
///     bottom are sites left deliberately caretless, and each says what would have to change
///     for a caret to be honest there.
/// </summary>
public class IterationColumnTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    private static CompilerError Fails(string src) =>
        Assert.Throws<CompilerError>(() => Gen(src));

    /// The 1-based column of the first occurrence of <paramref name="text"/> on the line the
    /// error reports, so the expectation reads as "under that text" rather than as a number
    /// that has to be recounted whenever the fixture moves.
    private static int ColumnOf(string src, int line, string text)
    {
        string[] lines = src.Split('\n');
        int i = lines[line - 1].IndexOf(text, StringComparison.Ordinal);
        Assert.True(i >= 0, $"'{text}' is not on line {line}: {lines[line - 1]}");
        return i + 1;
    }

    private static void PointsAt(string src, int line, string text)
    {
        var ex = Fails(src);
        Assert.Equal(line, ex.Line);
        Assert.Equal(ColumnOf(src, line, text), ex.Column);
    }

    // ---- the element of a sequence written at the loop ------------------------------------

    [Fact]
    public void ListElement_PointsAtTheElement()
    {
        const string src =
            "def main():\n" +
            "    for a in [1, 2.5]:\n" +
            "        pass\n";
        PointsAt(src, 2, "2.5");
    }

    [Fact]
    public void PairElement_PointsAtWhichHalfIsNotConstant()
    {
        // The second half is the one that is not a constant, and the message says "both values
        // in each pair", so without a caret the reader has no way to tell which. Evaluating
        // both halves before the check is what makes this answerable.
        const string src =
            "def main():\n" +
            "    for a, b in [(1, 2), (3, 4.5)]:\n" +
            "        pass\n";
        PointsAt(src, 2, "4.5");
    }

    [Fact]
    public void ZipElement_PointsAtTheElement()
    {
        const string src =
            "def main():\n" +
            "    for a, b in zip([1, 2], [3, 4.5]):\n" +
            "        pass\n";
        PointsAt(src, 2, "4.5");
    }

    [Fact]
    public void ReversedElement_PointsAtTheElement()
    {
        const string src =
            "def main():\n" +
            "    for v in reversed([1, 2.5]):\n" +
            "        pass\n";
        PointsAt(src, 2, "2.5");
    }

    [Fact]
    public void EnumerateElement_PointsAtTheElement()
    {
        const string src =
            "def main():\n" +
            "    for i, v in enumerate([1, 2.5]):\n" +
            "        pass\n";
        PointsAt(src, 2, "2.5");
    }

    // ---- a range() bound ------------------------------------------------------------------

    [Fact]
    public void RangeStepOfZero_PointsAtTheZero()
    {
        // The issue's own example of a caret worth having: the step is the only wrong thing in
        // the line, and it is the last character of three arguments that all look alike.
        const string src =
            "def main():\n" +
            "    for i in range(0, 10, 0):\n" +
            "        pass\n";
        PointsAt(src, 2, "0):");
    }

    [Fact]
    public void RangeAsIterable_StepOfZero_PointsAtTheZero()
    {
        // The parenthesised spelling reaches a different site with the same message, because
        // the parser files a bare `range(...)` as bounds and anything else as an iterable.
        // Both have to point at the same character or the caret depends on the parentheses.
        const string src =
            "def main():\n" +
            "    for i in (range(0, 10, 0)):\n" +
            "        pass\n";
        PointsAt(src, 2, "0)):");
    }

    [Fact]
    public void RangeAsIterable_PointsAtTheArgumentThatIsNotConstant()
    {
        // The first argument is a constant and the second is not, and the message is plural.
        const string src =
            "def main(n: uint8):\n" +
            "    for i in (range(0, n)):\n" +
            "        pass\n";
        PointsAt(src, 2, "n))");
    }

    [Fact]
    public void EnumerateRange_PointsAtTheArgumentThatIsNotConstant()
    {
        const string src =
            "def main(n: uint8):\n" +
            "    for i, v in enumerate(range(0, n)):\n" +
            "        pass\n";
        PointsAt(src, 2, "n))");
    }

    [Fact]
    public void RangeWithNoArguments_PointsAtTheCallee()
    {
        // There is no argument to point at, so the callee is what there is. That is the
        // convention Parser.cs records for a CallExpr, which carries no position of its own.
        const string src =
            "def main():\n" +
            "    for i in (range()):\n" +
            "        pass\n";
        PointsAt(src, 2, "range");
    }

    // ---- the iterable itself --------------------------------------------------------------

    [Fact]
    public void UnsupportedIterable_PointsAtTheIterable()
    {
        const string src =
            "def main(n: uint8):\n" +
            "    for v in n:\n" +
            "        pass\n";
        PointsAt(src, 2, "n:");
    }

    [Fact]
    public void IteratorProtocol_PointsAtTheObject()
    {
        const string src =
            "class Counter:\n" +
            "    def __init__(self):\n" +
            "        self.n: uint8 = 0\n" +
            "    def __iter__(self):\n" +
            "        return self\n" +
            "    def __next__(self) -> uint8:\n" +
            "        self.n = self.n + 1\n" +
            "        return self.n\n" +
            "def main():\n" +
            "    c = Counter()\n" +
            "    for v in c:\n" +
            "        pass\n";
        PointsAt(src, 11, "c:");
    }

    // ---- the assert condition ---------------------------------------------------------------

    [Fact]
    public void FailedAssert_PointsAtTheCondition()
    {
        const string src =
            "def main():\n" +
            "    assert 0, \"nope\"\n";
        PointsAt(src, 2, "0,");
    }

    // ---- sites left unlocated on purpose ----------------------------------------------------

    [Fact]
    public void ElementsResolvedThroughAName_StayUnlocated()
    {
        // The dict was written somewhere else, and the entry nodes belong to whichever file
        // wrote it. ASTNode carries no file, so blaming an entry would state that file's
        // column against this file's line. Measured on a two-module program: an @inline in
        // helper.py fed a list literal from main.py reports helper.py's line, and the element
        // is on main.py's. No caret is the honest answer until a node knows its file.
        const string src =
            "d = {1: 10, 2.5: 20}\n" +
            "def main():\n" +
            "    for k in d:\n" +
            "        pass\n";
        var ex = Fails(src);
        Assert.Contains("has to be a constant", ex.Message);
        Assert.Equal(CompilerError.Unlocated, ex.Column);
    }

    [Fact]
    public void ATupleElement_StaysUnlocated_UntilTuplesAreStamped()
    {
        // The node in hand IS the right one to blame, and it is passed. It reports no column
        // because the parser does not stamp a TupleExpr, so this asserts the current ceiling
        // rather than a decision: stamping tuples lights this site up with no edit here.
        const string src =
            "def main():\n" +
            "    for a in [(1, 2), (3, 4)]:\n" +
            "        pass\n";
        var ex = Fails(src);
        Assert.Contains("nowhere to put the second value", ex.Message);
        Assert.Equal(CompilerError.Unlocated, ex.Column);
    }

    [Fact]
    public void TheLoopTargetNames_HaveNoNodeAndStayUnlocated()
    {
        // This one is about the names after `for`, which the AST keeps as strings on the
        // statement and not as nodes. There is nothing to point at, and inventing a column
        // here is exactly what the issue exists to stop.
        const string src =
            "d = {1: 10, 2: 20}\n" +
            "def main():\n" +
            "    for k in d.items():\n" +
            "        pass\n";
        var ex = Fails(src);
        Assert.Contains("nowhere to put the value", ex.Message);
        Assert.Equal(CompilerError.Unlocated, ex.Column);
    }
}
