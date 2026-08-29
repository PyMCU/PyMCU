using Xunit;
using PyMCU.Common;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;

namespace PyMCU.UnitTests;

/// <summary>
/// Where the caret lands for the diagnostics `Assign.cs` raises (issue #177). An assignment has
/// a target and a value, and every message here is about exactly one of them, so the statement's
/// line on its own left the reader to guess which side to change.
///
/// The sites covered are the ones a triggering program was found for. `Assign.cs` has more
/// unlocated sites than this file has tests, and the ones with no probe were left alone rather
/// than given a node nobody watched land: the commit message lists them by line.
/// </summary>
public class AssignColumnTests
{
    private const string Prelude =
        "def uart_write_str(s: const[str]):\n" +
        "    pass\n" +
        "def uart_write(c: uint8):\n" +
        "    pass\n" +
        "def uart_write_decimal_u8(v: uint8):\n" +
        "    pass\n";

    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(Prelude + src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    private static CompilerError Fails(string src) =>
        Assert.ThrowsAny<CompilerError>(() => Gen(src));

    /// Asserts the diagnostic lands on the line containing <paramref name="onLine"/>, under
    /// <paramref name="text"/>. The line is found by its own text rather than counted, so the
    /// shared Prelude can grow without renumbering every expectation.
    private static void PointsAt(string src, string onLine, string text)
    {
        var ex = Fails(src);
        string[] lines = (Prelude + src).Split('\n');
        int line = Array.FindIndex(lines, l => l.Contains(onLine, StringComparison.Ordinal)) + 1;
        Assert.True(line > 0, $"no line of the fixture contains '{onLine}'");
        Assert.Equal(line, ex.Line);
        int col = lines[line - 1].IndexOf(text, StringComparison.Ordinal) + 1;
        Assert.True(col > 0, $"'{text}' is not on line {line}: {lines[line - 1]}");
        Assert.Equal(col, ex.Column);
    }

    // ---- the target ------------------------------------------------------------------------

    [Fact]
    public void RebindingAFunctionName_PointsAtTheName()
    {
        const string src =
            "def handler() -> uint8:\n" +
            "    return 1\n" +
            "def main():\n" +
            "    global handler\n" +
            "    handler = 5\n";
        PointsAt(src, "handler = 5", "handler");
    }

    [Fact]
    public void AssigningAFieldTheClassDoesNotHave_PointsAtTheField()
    {
        // `b.bb = 3` is one character away from working and the message says so; the caret puts
        // it under the name to correct rather than under the object, which is fine.
        const string src =
            "class Box:\n" +
            "    def __init__(self):\n" +
            "        self.a: uint8 = 0\n" +
            "def main():\n" +
            "    b = Box()\n" +
            "    b.bb = 3\n";
        PointsAt(src, "b.bb = 3", "bb");
    }

    [Fact]
    public void AugmentedAssignmentToDotValue_PointsAtTheMember()
    {
        const string src =
            "def main():\n" +
            "    v: uint8 = 1\n" +
            "    v.value += 1\n";
        PointsAt(src, "v.value += 1", "value");
    }

    // ---- the value -------------------------------------------------------------------------

    [Fact]
    public void AnOutOfRangeLiteral_PointsAtTheLiteral()
    {
        // The one direct `throw new ValueError` in the file that had a line and no column while
        // holding the literal its own message quotes.
        const string src =
            "def main():\n" +
            "    v: uint8 = 300\n";
        PointsAt(src, "v: uint8 = 300", "300");
    }

    [Fact]
    public void StrJoinWithTwoArguments_PointsAtTheMethod()
    {
        const string src =
            "def main():\n" +
            "    s = \",\".join([\"a\"], [\"b\"])\n";
        PointsAt(src, ".join(", "join");
    }

    [Fact]
    public void ASliceCopyOfTheWrongLength_PointsAtTheSource()
    {
        // The target's length comes from a declaration that may be anywhere; the source slice is
        // written here, so it is the half the reader can act on from this line.
        const string src =
            "def main():\n" +
            "    a: uint8[4] = [1, 2, 3, 4]\n" +
            "    b: uint8[4] = [5, 6, 7, 8]\n" +
            "    a[0:2] = b[0:3]\n";
        PointsAt(src, "a[0:2] = b[0:3]", "b[0:3]");
    }

    [Fact]
    public void TooManyValuesToUnpack_PointsAtTheFirstSurplusValue()
    {
        const string src =
            "def main():\n" +
            "    a: uint8 = 0\n" +
            "    b: uint8 = 0\n" +
            "    a, b = 1, 2, 3\n";
        PointsAt(src, "a, b = 1, 2, 3", "3");
    }

    [Fact]
    public void AStarredTargetOnAMultiReturn_PointsAtTheCallee()
    {
        const string src =
            "def two() -> uint8:\n" +
            "    return 1\n" +
            "def main():\n" +
            "    a: uint8 = 0\n" +
            "    a, *rest = two()\n";
        PointsAt(src, "a, *rest = two()", "two");
    }

    // ---- the direction with nothing to point at ---------------------------------------------

    [Fact]
    public void TooFewValuesToUnpack_StaysUnlocated()
    {
        // The mirror of the surplus case, and deliberately not symmetric. With fewer values than
        // targets there is no surplus element to mark, and a TupleExpr carries no position of
        // its own, so marking any element would blame one the reader wrote correctly.
        const string src =
            "def main():\n" +
            "    a: uint8 = 0\n" +
            "    b: uint8 = 0\n" +
            "    c: uint8 = 0\n" +
            "    a, b, c = 1, 2\n";
        var ex = Fails(src);
        Assert.Contains("tuple unpacking size mismatch", ex.Message);
        Assert.Equal(CompilerError.Unlocated, ex.Column);
    }
}
