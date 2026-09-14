using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU#285. After a constant range short enough to unroll, the loop variable read 0: the
/// unroller bound the name to a constant per iteration and dropped the binding on exit
/// without storing the last value. After a runtime range loop it held `stop`, the first
/// value not visited, where Python leaves the last one visited.
///
/// Both now owe a store only when the source reads the name after the loop; a variable no
/// one reads afterwards costs nothing, which is the common case.
/// </summary>
public class RangeLoopVarAfterLoopTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    private static List<Instruction> Main(string src) => Gen(src).Functions.Single(f => f.Name == "main").Body;

    private const string Prelude = "from pymcu.types import uint8, uint16\n\n";

    [Fact]
    public void AfterAnUnrolledRange_TheLastValueIsStored()
    {
        var body = Main(Prelude +
            "def main():\n" +
            "    x: uint8 = 0\n" +
            "    for i in range(3):\n" +
            "        x = x + i\n" +
            "    y: uint8 = i\n\n" +
            "main()\n");
        Assert.DoesNotContain(body, i => i is JumpIfGreaterOrEqual);
        Assert.Contains(body, i => i is Copy { Src: Constant { Value: 2 }, Dst: Variable { Name: "main.i" } });
    }

    [Fact]
    public void AtModuleLevel_TheLastValueIsStoredToo()
    {
        var body = Main(Prelude +
            "x: uint8 = 0\n" +
            "for i in range(3):\n" +
            "    x = x + i\n" +
            "y: uint8 = i\n");
        Assert.Contains(body, i => i is Copy { Src: Constant { Value: 2 }, Dst: Variable { Name: "main.i" } });
    }

    [Fact]
    public void AnUnrolledRangeWithABreak_StoresEveryIteration()
    {
        var body = Main(Prelude +
            "def main():\n" +
            "    lim: uint8 = 4\n" +
            "    for n in range(6):\n" +
            "        if n == lim:\n" +
            "            break\n" +
            "    y: uint8 = n\n\n" +
            "main()\n");
        for (int k = 0; k < 6; k++)
            Assert.Contains(body, i => i is Copy { Src: Constant { Value: var v }, Dst: Variable { Name: "main.n" } } && v == k);
    }

    [Fact]
    public void AfterARuntimeRange_TheCounterStepsBack()
    {
        var body = Main(Prelude +
            "def main():\n" +
            "    x: uint8 = 0\n" +
            "    for k in range(20):\n" +
            "        x = x + 1\n" +
            "    y: uint8 = k\n\n" +
            "main()\n");
        int end = body.FindLastIndex(i => i is JumpIfGreaterOrEqual);
        Assert.Contains(body.Skip(end), i => i is JumpIfEqual { Src1: Variable { Name: "main.k" }, Src2: Constant { Value: 0 } });
        Assert.Contains(body.Skip(end), i => i is AugAssign { Op: PyMCU.IR.BinaryOp.Sub, Target: Variable { Name: "main.k" } });
    }

    [Fact]
    public void AVariableNoOneReadsAfterTheLoop_CostsNothing()
    {
        var body = Main(Prelude +
            "def main():\n" +
            "    x: uint8 = 0\n" +
            "    for k in range(20):\n" +
            "        x = x + k\n" +
            "    for i in range(3):\n" +
            "        x = x + i\n\n" +
            "main()\n");
        Assert.DoesNotContain(body, i => i is JumpIfEqual);
        Assert.DoesNotContain(body, i => i is AugAssign { Op: PyMCU.IR.BinaryOp.Sub });
        Assert.DoesNotContain(body, i => i is Copy { Dst: Variable { Name: "main.i" } });
    }

    [Fact]
    public void AReadInsideALaterLoop_Counts()
    {
        var body = Main(Prelude +
            "def main():\n" +
            "    x: uint8 = 0\n" +
            "    for i in range(3):\n" +
            "        x = x + i\n" +
            "    while x < 100:\n" +
            "        x = x + i\n\n" +
            "main()\n");
        Assert.Contains(body, i => i is Copy { Src: Constant { Value: 2 }, Dst: Variable { Name: "main.i" } });
    }

    [Fact]
    public void ABreak_SkipsTheStepBack()
    {
        var body = Main(Prelude +
            "def main():\n" +
            "    lim: uint8 = 4\n" +
            "    for k in range(20):\n" +
            "        if k == lim:\n" +
            "            break\n" +
            "    y: uint8 = k\n\n" +
            "main()\n");
        int stepBack = body.FindIndex(i => i is AugAssign { Op: PyMCU.IR.BinaryOp.Sub, Target: Variable { Name: "main.k" } });
        Assert.True(stepBack >= 0, "the read after the loop asks for a step back on the exit-test path");
        // The break's jump lands past the step back: the counter it leaves is the one Python leaves.
        int exitTest = body.FindIndex(i => i is JumpIfGreaterOrEqual { Src1: Variable { Name: "main.k" } });
        var breakJump = body.Skip(exitTest).OfType<Jump>().First(j => body.FindIndex(i => i is Label l && l.Name == j.Target) > stepBack);
        int breakTarget = body.FindIndex(i => i is Label l && l.Name == breakJump.Target);
        Assert.True(breakTarget > stepBack, "break lands after the step back, not on it");
    }
}
