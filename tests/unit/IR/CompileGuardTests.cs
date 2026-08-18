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
