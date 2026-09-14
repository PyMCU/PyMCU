using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU#299. A tuple bound to a name and a list bound to a name share their storage here, so
/// a write through the name was accepted on the tuple. CPython raises
/// `TypeError: 'tuple' object does not support item assignment` on the same program, and a
/// compiler that accepts it teaches that tuples are writable on this target.
///
/// Immutability is a property of the NAME, not of the storage, and the binding is where the
/// tuple-ness is known: both the short form that binds for compile-time unrolling and the long
/// form that gets a fixed array since #297 pass through the same two sites.
/// </summary>
public class TupleIsNotWritableTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    private static string Refusal(string src) =>
        Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(src)).Message;

    [Fact]
    public void AWriteThroughAModuleLevelTupleIsRefused()
    {
        string msg = Refusal(
            "T = (1, 2, 3)\n\n" +
            "def main():\n" +
            "    T[0] = 5\n" +
            "    y = T[0]\n");
        Assert.Contains("'T' is a tuple", msg);
        Assert.Contains("list", msg);
    }

    [Fact]
    public void TheLongFormIsRefusedToo()
    {
        // Nine elements: past the unroll limit, so this is the fixed-array path of #297. Both
        // lengths built clean before, which is what made the acceptance look deliberate.
        Assert.Contains("'T' is a tuple", Refusal(
            "T = (1, 2, 3, 4, 5, 6, 7, 8, 9)\n\n" +
            "def main():\n" +
            "    T[0] = 5\n" +
            "    y = T[0]\n"));
    }

    [Fact]
    public void ATupleBoundInsideAFunctionIsRefused()
    {
        Assert.Contains("'t' is a tuple", Refusal(
            "def main():\n" +
            "    t = (1, 2, 3)\n" +
            "    t[0] = 5\n" +
            "    y = t[0]\n"));
    }

    [Fact]
    public void ASliceWriteThroughATupleIsRefusedByTheSameSentence()
    {
        Assert.Contains("'T' is a tuple", Refusal(
            "T = (1, 2, 3, 4)\n\n" +
            "def main():\n" +
            "    T[0:2] = [7, 8]\n" +
            "    y = T[0]\n"));
    }

    [Fact]
    public void TheRefusalPointsAtTheWriteAndNotAtTheBinding()
    {
        var ex = Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(
            "T = (1, 2, 3)\n" +
            "\n" +
            "\n" +
            "def main():\n" +
            "    T[0] = 5\n" +
            "    y = T[0]\n"));
        Assert.Equal(5, ex.Line);
    }

    [Fact]
    public void ReadingATupleIsUnchanged()
    {
        Assert.NotNull(Gen(
            "T = (1, 2, 3)\n\n" +
            "def main():\n" +
            "    y = T[0]\n" +
            "    for v in T:\n" +
            "        y = y + v\n"));
    }

    [Fact]
    public void AListIsStillWritable()
    {
        Assert.NotNull(Gen(
            "L = [1, 2, 3]\n\n" +
            "def main():\n" +
            "    L[0] = 5\n" +
            "    y = L[0]\n"));
    }

    [Fact]
    public void ANameReboundFromATupleToAListIsWritableAgain()
    {
        // The refusal is about the name's current binding, not about a name that was once a
        // tuple: a set that only ever grows would refuse a legal program.
        Assert.NotNull(Gen(
            "def main():\n" +
            "    t = (1, 2, 3)\n" +
            "    y = t[0]\n" +
            "    t = [1, 2, 3]\n" +
            "    t[0] = 5\n" +
            "    y = t[0]\n"));
    }
}
