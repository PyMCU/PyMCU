using PyMCU.Common;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

public class CompileGuardTests
{
    private static ProgramIR Gen(string src)
    {
        var tokens = new Lexer(src).Tokenize();
        var ast = new Parser(tokens).ParseProgram();
        return new IRGenerator().Generate(ast, new Dictionary<string, ProgramNode>(), new DeviceConfig());
    }

    private static List<Instruction> MainBody(ProgramIR ir) =>
        ir.Functions.First(f => f.Name == "main").Body;

    private const string OrGuard =
        "@inline\ndef sel(name: str) -> uint8:\n" +
        "    if name == \"A\" or name == \"B\":\n" +
        "        raise CompileError(\"A/B not supported\")\n" +
        "    return 0xAA\n";

    private const string ThreeTermGuard =
        "@inline\ndef sel3(name: str) -> uint8:\n" +
        "    if name == \"A\" or name == \"B\" or name == \"C\":\n" +
        "        raise CompileError(\"A/B/C not supported\")\n" +
        "    return 0xAA\n";

    private const string HalStyleGuard =
        "@inline\ndef pull_up(name: str) -> uint8:\n" +
        "    if name == \"RB0\":\n" +
        "        return 1\n" +
        "    elif name == \"RB1\":\n" +
        "        return 2\n" +
        "    else:\n" +
        "        raise NotImplementedError(\"no pull-up on this pin\")\n";

    [Fact]
    public void DeadOrBranch_IsPruned_InsteadOfFiringTheGuard()
    {
        var body = MainBody(Gen(OrGuard + "def main():\n    x: uint8 = sel(\"C\")\n"));

        Assert.Contains(body, i => i is Copy { Src: Constant { Value: 0xAA } });
    }

    [Fact]
    public void DeadThreeTermOrBranch_IsPruned()
    {
        var body = MainBody(Gen(ThreeTermGuard + "def main():\n    x: uint8 = sel3(\"D\")\n"));

        Assert.Contains(body, i => i is Copy { Src: Constant { Value: 0xAA } });
    }

    [Fact]
    public void LiveOrBranch_StillFiresTheGuard()
    {
        var ex = Assert.Throws<ArchitectureError>(
            () => Gen(OrGuard + "def main():\n    x: uint8 = sel(\"B\")\n"));

        Assert.Contains("A/B not supported", ex.Message);
    }

    [Fact]
    public void InlineGuard_WithARuntimeException_FailsCompilationInsteadOfBeingDiscarded()
    {
        var ex = Assert.Throws<ArchitectureError>(
            () => Gen(HalStyleGuard + "def main():\n    x: uint8 = pull_up(\"RC0\")\n"));

        Assert.Contains("NotImplementedError", ex.Message);
        Assert.Contains("no pull-up on this pin", ex.Message);
    }

    [Fact]
    public void InlineGuard_OnTheValidPath_SignalsNoError()
    {
        var body = MainBody(Gen(HalStyleGuard + "def main():\n    x: uint8 = pull_up(\"RB1\")\n"));

        Assert.DoesNotContain(body, i => i is SignalError);
        Assert.Contains(body, i => i is Copy { Src: Constant { Value: 2 } });
    }

    // A lookup: probe a table, and raise when the search runs out. The trailing raise sits at
    // the expansion's entry depth, which is what the abort rule reads as "always reached" --
    // and it is not: the loop exits on data the compiler cannot see. Regression for PyMCU#73,
    // where this shape stopped FixedDict from compiling at all.
    private const string SearchThenRaise =
        "@inline\ndef find(table: bytearray, key: uint8) -> uint8:\n" +
        "    n: uint8 = 0\n" +
        "    while n < 4:\n" +
        "        if table[n] == key:\n" +
        "            return n\n" +
        "        n = n + 1\n" +
        "    raise KeyError\n";

    [Fact]
    public void RaiseAfterASearchLoop_InsideAnInline_DoesNotAbortCompilation()
    {
        var ir = Gen(SearchThenRaise +
            "def main():\n    t: uint8[4] = [1, 2, 3, 4]\n    i: uint8 = find(t, 3)\n");

        Assert.NotNull(ir);
    }

    [Fact]
    public void RaiseWithNoLoopBefore_InsideAnInline_StillAbortsCompilation()
    {
        var src =
            "@inline\ndef nope(x: uint8) -> uint8:\n" +
            "    raise KeyError\n" +
            "def main():\n    y: uint8 = nope(1)\n";

        var ex = Assert.Throws<ArchitectureError>(() => Gen(src));

        Assert.Contains("KeyError", ex.Message);
    }

    [Fact]
    public void RuntimeConditionalGuard_DoesNotFireAtCompileTime()
    {
        var src =
            "def guard(v: uint8) -> uint8:\n" +
            "    if v == 1:\n" +
            "        raise CompileError(\"cannot be verified\")\n" +
            "    return 0x55\n" +
            "def main():\n    a: uint8 = 3\n    b: uint8 = guard(a)\n";

        var ir = Gen(src);

        Assert.NotNull(ir);
    }
}
