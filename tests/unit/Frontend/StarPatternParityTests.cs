using PyMCU.Frontend;
using Xunit;

namespace PyMCU.Tests.Frontend;

/// <summary>
/// A star in a sequence pattern (#401). The CPython front end read `*tail` as one more capture, so
/// `case [head, *tail]` took its arm for a sequence of any length and bound the head to the wrong
/// slot; the hand-written parser stops at the star. Both refuse it now, with the same sentence.
/// Written as a parity assertion because agreement is the property that was broken.
/// </summary>
public class StarPatternParityTests
{
    private static string? Csharp(string src) =>
        Record.Exception(() => new Parser(new Lexer(src).Tokenize()).ParseProgram())?.Message;

    private static string? CPython(string src) =>
        Record.Exception(() => PythonAstReader.ParseSource(src, "main.py"))?.Message;

    private static void BothRefuse(string src, string sentence)
    {
        var cs = Csharp(src);
        var py = CPython(src);
        Assert.True(cs != null && cs.Contains(sentence), "hand-written parser: " + (cs ?? "accepted it"));
        Assert.True(py != null && py.Contains(sentence), "CPython front end: " + (py ?? "accepted it"));
    }

    private const string Star = "a star pattern (`*rest`) in a sequence pattern is not supported";

    [Fact]
    public void AStarInASequencePatternIsRefused() =>
        BothRefuse("match data:\n    case [head, *tail]:\n        pass\n", Star);

    [Fact]
    public void APlainSequencePatternStillParses()
    {
        const string src = "match d:\n    case [x, y]:\n        pass\n";
        Assert.Null(Csharp(src));
        Assert.Null(CPython(src));
    }
}
