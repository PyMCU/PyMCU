using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// A tuple/list-literal `for` loop unrolls at compile time. CPython leaves the loop
/// variable(s) bound to whatever the LAST assignment made to them, through full
/// completion, a `break`, a `return`, or an uncaught `raise` -- a zero-trip iterable never
/// touches the name at all. The unroller used to materialize a real, physically written
/// value only when the body had a literal `break`, and even then only for the single-name
/// form; a pair-unpack never materialized at all, a `return`/`raise` was invisible to the
/// check, and a guaranteed-complete loop left only a compile-time-only fold with no backing
/// store, which a LATER unrelated write to the same bare name could leave stale.
/// </summary>
public class TupleLoopVarAfterLoopTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    private static List<Instruction> Main(string src) => Gen(src).Functions.Single(f => f.Name == "main").Body;

    private const string Prelude = "from pymcu.types import uint8\n\n";

    [Fact]
    public void APairUnpackWithABreak_MaterializesBothNames()
    {
        // for a, b in ((1, 2), (3, 4)): break
        // print(a); print(b)  -- CPython: 1, 2 (what the break's own iteration bound)
        var body = Main(Prelude +
            "def main():\n" +
            "    a: uint8 = 9\n" +
            "    b: uint8 = 9\n" +
            "    for a, b in ((1, 2), (3, 4)):\n" +
            "        break\n" +
            "    y: uint8 = a\n" +
            "    z: uint8 = b\n\n" +
            "main()\n");
        Assert.Contains(body, i => i is Copy { Src: Constant { Value: 1 }, Dst: Variable { Name: "main.a" } });
        Assert.Contains(body, i => i is Copy { Src: Constant { Value: 2 }, Dst: Variable { Name: "main.b" } });
    }

    [Fact]
    public void ARaiseInsideATupleLoop_MaterializesTheInProgressIteration()
    {
        // try:
        //     for v in (1, 2): raise ValueError()
        // except ValueError: print(v)  -- CPython: 1, not the tuple's last element
        var body = Main(Prelude +
            "from pymcu.types import uint8\n" +
            "def main():\n" +
            "    v: uint8 = 9\n" +
            "    try:\n" +
            "        for v in (1, 2):\n" +
            "            raise ValueError()\n" +
            "    except ValueError:\n" +
            "        y: uint8 = v\n\n" +
            "main()\n");
        Assert.Contains(body, i => i is Copy { Src: Constant { Value: 1 }, Dst: Variable { Name: "main.v" } });
    }

    [Fact]
    public void AGuaranteedCompleteTupleLoop_StoresTheLastValueForReal()
    {
        // for v in (1, 2): pass
        // y: uint8 = v  -- CPython: 2, and it must be a REAL write, not just a fold a
        // later unrelated write to the same bare name could leave stale.
        var body = Main(Prelude +
            "def main():\n" +
            "    v: uint8 = 9\n" +
            "    for v in (1, 2):\n" +
            "        pass\n" +
            "    y: uint8 = v\n\n" +
            "main()\n");
        Assert.Contains(body, i => i is Copy { Src: Constant { Value: 2 }, Dst: Variable { Name: "main.v" } });
    }

    [Fact]
    public void AZeroTripTupleLoop_LeavesAnEarlierLoopsRealValueInPlace()
    {
        // for v in (1, 2): pass
        // for v in (): pass        -- zero trips: never touches v
        // y: uint8 = v              -- CPython: 2, the FIRST loop's last value
        var body = Main(Prelude +
            "def main():\n" +
            "    v: uint8 = 9\n" +
            "    for v in (1, 2):\n" +
            "        pass\n" +
            "    for v in ():\n" +
            "        pass\n" +
            "    y: uint8 = v\n\n" +
            "main()\n");
        Assert.Contains(body, i => i is Copy { Src: Constant { Value: 2 }, Dst: Variable { Name: "main.v" } });
    }

    [Fact]
    public void ANestedTupleLoopReusingTheOuterName_TheInnersLastValueWins()
    {
        // for v in (1, 2):
        //     for v in (7, 8): pass
        // y: uint8 = v  -- CPython: 8 (the INNER loop's own last write, which runs last)
        var body = Main(Prelude +
            "def main():\n" +
            "    v: uint8 = 9\n" +
            "    for v in (1, 2):\n" +
            "        for v in (7, 8):\n" +
            "            pass\n" +
            "    y: uint8 = v\n\n" +
            "main()\n");
        Assert.Contains(body, i => i is Copy { Src: Constant { Value: 8 }, Dst: Variable { Name: "main.v" } });
    }
}
