using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// Regression tests for the value of an @inline call that returns a different constant on
/// each of several run-time branches (issue #132). The result was tracked as the FIRST
/// return's constant, so every consumer that folds a constant right-hand side dropped the
/// store: `self._field = helper(x)` emitted no store at all and `callee(helper(x))` bound
/// the parameter to the first return. PWM.set_freq always programmed prescaler 1.
///
/// The seed comes from a ptr load, not a literal, so these measure the run-time path. A
/// reproducer built out of literals folds the branch away and passes against the bug.
/// </summary>
public class InlineMultiReturnTests
{
    private static ProgramIR Gen(string src)
    {
        var tokens = new Lexer(src).Tokenize();
        var ast = new Parser(tokens).ParseProgram();
        return new IRGenerator().Generate(ast, new Dictionary<string, ProgramNode>(), new DeviceConfig());
    }

    private static List<Instruction> MainBody(ProgramIR ir) =>
        ir.Functions.First(f => f.Name == "main").Body;

    // A run-time seed (G.value) selects between two constant returns, so neither is the
    // value of the call: the callee decides at run time.
    private const string Preamble =
        "from pymcu.types import uint8, const, inline, ptr\n" +
        "G: ptr[uint8] = ptr(0x3E)\n" +
        "@inline\n" +
        "def pick(n: uint8) -> uint8:\n" +
        "    if n > 100:\n" +
        "        return 1\n" +
        "    else:\n" +
        "        return 5\n";

    private const string BoxClass =
        "class Box:\n" +
        "    def __init__(self):\n" +
        "        self._a: uint8 = 0\n";

    [Fact]
    public void MultiReturnInline_AsFieldStoreRhs_EmitsTheStore()
    {
        var body = MainBody(Gen(Preamble + BoxClass +
            "def main():\n" +
            "    seed: uint8 = G.value\n" +
            "    box = Box()\n" +
            "    box._a = pick(seed)\n"));

        // Both arms write the same result temp ...
        var resultTemps = body.OfType<Copy>()
            .Where(c => c.Src is Constant { Value: 1 } or Constant { Value: 5 })
            .Select(c => c.Dst).OfType<Temporary>().Select(t => t.Name).Distinct().ToList();
        Assert.Single(resultTemps);

        // ... and the field store that consumes it survives, reading that temp. Before the
        // fix there was no store into main.box__a after the constructor's zero at all.
        Assert.Contains(body, i =>
            i is Copy { Src: Temporary st, Dst: Variable { Name: "main.box__a" } }
            && st.Name == resultTemps[0]);
    }

    [Fact]
    public void MultiReturnInline_AsInlineArgument_BindsTheRuntimeValue()
    {
        var body = MainBody(Gen(Preamble +
            "@inline\n" +
            "def sink(v: uint8):\n" +
            "    G.value = v\n" +
            "def main():\n" +
            "    seed: uint8 = G.value\n" +
            "    sink(pick(seed))\n"));

        // The callee's parameter slot is loaded from the result temp, not from a constant.
        // Before the fix the argument folded to 1 and no Copy into the slot was emitted.
        Assert.Contains(body, i =>
            i is Copy { Src: Temporary, Dst: Variable pv } && pv.Name.EndsWith("sink.v"));
        Assert.DoesNotContain(body, i =>
            i is Copy { Src: Constant, Dst: Variable pv2 } && pv2.Name.EndsWith("sink.v"));
    }

    [Fact]
    public void MultiReturnInline_AsFieldStoreRhs_KeepsBothArmsLive()
    {
        var body = MainBody(Gen(Preamble + BoxClass +
            "def main():\n" +
            "    seed: uint8 = G.value\n" +
            "    box = Box()\n" +
            "    box._a = pick(seed)\n" +
            "    G.value = box._a\n"));

        // The MMIO store must read the field, not a constant folded from the first return.
        Assert.Contains(body, i => i is Copy { Src: Variable { Name: "main.box__a" }, Dst: MemoryAddress });
        Assert.DoesNotContain(body, i => i is Copy { Src: Constant, Dst: MemoryAddress });
    }

    // The guard on the fix: when the selecting condition folds at compile time only ONE
    // return is ever visited, so a genuinely constant result must keep its zero-cost
    // folding. Killing the constant unconditionally would deoptimize the whole ZCA HAL.
    [Fact]
    public void ConstSelectedInline_StillFoldsToOneConstant()
    {
        var body = MainBody(Gen(
            "from pymcu.types import uint8, const, inline, ptr\n" +
            "G: ptr[uint8] = ptr(0x3E)\n" +
            "@inline\n" +
            "def pick(n: const[uint8]) -> uint8:\n" +
            "    if n == 1:\n" +
            "        return 10\n" +
            "    elif n == 2:\n" +
            "        return 20\n" +
            "    else:\n" +
            "        return 30\n" +
            BoxClass +
            "def main():\n" +
            "    box = Box()\n" +
            "    box._a = pick(2)\n" +
            "    G.value = box._a\n"));

        Assert.Contains(body, i => i is Copy { Src: Constant { Value: 20 }, Dst: MemoryAddress });
        Assert.DoesNotContain(body, i => i is Copy { Src: Constant { Value: 10 } });
        Assert.DoesNotContain(body, i => i is Copy { Src: Constant { Value: 30 } });
    }
}
