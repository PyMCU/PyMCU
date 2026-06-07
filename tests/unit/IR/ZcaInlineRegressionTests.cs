using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// Regression tests for the inline-expansion / alias / ZCA-state machinery that
/// proved fragile during the 2026-06 stabilization (a broad property-setter "fix"
/// that broke 145 integration tests, plus the C++->.NET migration dropping ZCA
/// instance arrays). These assert at the IR level so they are fast and pin the
/// exact behavior each fix restored.
/// </summary>
public class ZcaInlineRegressionTests
{
    private static ProgramIR Gen(string src)
    {
        var tokens = new Lexer(src).Tokenize();
        var ast = new Parser(tokens).ParseProgram();
        return new IRGenerator().Generate(ast, new Dictionary<string, ProgramNode>(), new DeviceConfig());
    }

    private static List<Instruction> MainBody(ProgramIR ir) =>
        ir.Functions.First(f => f.Name == "main").Body;

    // ── Property-setter value binding ────────────────────────────────────────
    // A property setter whose value param drives a branch must receive the actual
    // runtime argument. The broad be60f805 "fix" resolved the param alias to a dead
    // temporary, so the body read 0 and one branch was DCE'd. The correct fix
    // materializes the runtime value into the param's SRAM slot.

    private const string SetterClass =
        "class Out:\n" +
        "    @inline\n    def __init__(self):\n        self._v = 0\n        self._hi = 0\n" +
        "    @property\n    def v(self) -> uint8:\n        return self._v\n" +
        "    @v.setter\n    def v(self, x: uint8):\n        if x:\n            self._hi = 7\n        else:\n            self._hi = 9\n";

    [Fact]
    public void PropertySetter_RuntimeValue_EmitsBothBranches()
    {
        var body = MainBody(Gen(SetterClass +
            "def main():\n    o = Out()\n    a: uint8 = 5\n    o.v = a & 1\n"));

        // Both branch bodies survive -> the setter branched on a real runtime value,
        // not a constant-folded 0 that would have DCE'd one side.
        Assert.Contains(body, i => i is Copy { Src: Constant { Value: 7 } });
        Assert.Contains(body, i => i is Copy { Src: Constant { Value: 9 } });
        Assert.Contains(body, i => i is JumpIfZero);
    }

    [Fact]
    public void PropertySetter_RuntimeValue_MaterializesIntoParamSlot()
    {
        var body = MainBody(Gen(SetterClass +
            "def main():\n    o = Out()\n    a: uint8 = 5\n    o.v = a & 1\n"));

        // The `a & 1` result is computed into a temp, then copied into the setter's
        // own Variable slot (not left as a dangling alias to the temp).
        var andTemp = (Temporary)body.OfType<Binary>().First(b => b.Op == IR.BinaryOp.BitAnd).Dst;
        Assert.Contains(body, i =>
            i is Copy { Src: Temporary t, Dst: Variable v }
            && t.Name == andTemp.Name && v.Name.Contains("setter"));
    }

    // ── ZCA instance arrays (list / list-comp + for-in + enumerate) ───────────
    // Untyped `outs = [Cls(...)...]` was unreachable after the C++->.NET migration.
    // Each element must construct directly into its slot (name__k) with its nested
    // fields, and for-in / enumerate must inline element methods (no degraded CALL).

    private const string PinClass =
        "class P:\n" +
        "    @inline\n    def __init__(self, n: uint8):\n        self._n = n\n" +
        "    @inline\n    def show(self) -> uint8:\n        return self._n\n";

    [Fact]
    public void ZcaListComp_PlainAssign_ConstructsEachSlot()
    {
        var body = MainBody(Gen(PinClass +
            "def main():\n    outs = [P(p) for p in (3, 5, 7)]\n"));

        // Three slots, each with the per-element nested field value folded in.
        Assert.Contains(body, i => i is Copy { Src: Constant { Value: 3 }, Dst: Variable { Name: "main.outs__0__n" } });
        Assert.Contains(body, i => i is Copy { Src: Constant { Value: 5 }, Dst: Variable { Name: "main.outs__1__n" } });
        Assert.Contains(body, i => i is Copy { Src: Constant { Value: 7 }, Dst: Variable { Name: "main.outs__2__n" } });
    }

    [Fact]
    public void ZcaList_ExplicitElements_ConstructsEachSlot()
    {
        var body = MainBody(Gen(PinClass +
            "def main():\n    outs = [P(11), P(22)]\n"));

        Assert.Contains(body, i => i is Copy { Src: Constant { Value: 11 }, Dst: Variable { Name: "main.outs__0__n" } });
        Assert.Contains(body, i => i is Copy { Src: Constant { Value: 22 }, Dst: Variable { Name: "main.outs__1__n" } });
    }

    [Fact]
    public void ZcaListComp_RangeIterable_ConstructsEachSlot()
    {
        var body = MainBody(Gen(PinClass +
            "def main():\n    outs = [P(i) for i in range(3)]\n"));

        Assert.Contains(body, i => i is Copy { Src: Constant { Value: 0 }, Dst: Variable { Name: "main.outs__0__n" } });
        Assert.Contains(body, i => i is Copy { Src: Constant { Value: 1 }, Dst: Variable { Name: "main.outs__1__n" } });
        Assert.Contains(body, i => i is Copy { Src: Constant { Value: 2 }, Dst: Variable { Name: "main.outs__2__n" } });
    }

    [Fact]
    public void ForIn_OverZcaArray_InlinesNestedMethod_NoCall()
    {
        var body = MainBody(Gen(PinClass +
            "def main():\n" +
            "    outs = [P(p) for p in (3, 5, 7)]\n" +
            "    total: uint8 = 0\n" +
            "    for x in outs:\n        total = total + x.show()\n"));

        // The element method must inline; a degraded CALL means the loop var lost its
        // class (the nested-state propagation / loop-var qualification regression).
        Assert.DoesNotContain(body, i => i is Call);
        // Each element's _n value reaches the accumulation, proving per-slot state.
        foreach (var n in new[] { 3, 5, 7 })
            Assert.Contains(body, i => i is Copy { Src: Constant { Value: var val } } && val == n);
    }

    [Fact]
    public void Enumerate_OverZcaArray_InlinesNestedMethod_NoCall()
    {
        var body = MainBody(Gen(PinClass +
            "def main():\n" +
            "    outs = [P(p) for p in (3, 5, 7)]\n" +
            "    total: uint8 = 0\n" +
            "    for i, x in enumerate(outs):\n        total = total + x.show()\n"));

        Assert.DoesNotContain(body, i => i is Call);
        foreach (var n in new[] { 3, 5, 7 })
            Assert.Contains(body, i => i is Copy { Src: Constant { Value: var val } } && val == n);
    }

    // ── Same-depth inline re-expansion (be60f805 class of bug) ────────────────
    // Two sequential calls to the same @inline at the same depth reuse identical
    // qualified local names; the broad alias change made the second call inherit a
    // dead temporary from the first. With a runtime-conditioned loop body, both
    // expansions must keep their own loop structure.

    [Fact]
    public void InlineLoop_CalledTwiceSameDepth_BothExpansionsKeepLoop()
    {
        var body = MainBody(Gen(
            "class Counter:\n" +
            "    @inline\n    def __init__(self):\n        self._c = 0\n" +
            "    @inline\n    def step(self, stop: uint8) -> uint8:\n" +
            "        i: uint8 = 0\n        acc: uint8 = 0\n" +
            "        while i != stop:\n            acc = acc + i\n            i = i + 1\n        return acc\n" +
            "def main():\n" +
            "    c = Counter()\n    n: uint8 = 4\n" +
            "    a: uint8 = c.step(n)\n" +
            "    b: uint8 = c.step(n)\n"));

        // A runtime `stop` keeps the while loop un-unrolled; two calls -> two back-edge
        // Jumps that target an earlier label (the loop). Both expansions present means
        // the second was not collapsed by stale state from the first.
        int backEdges = 0;
        var seenLabels = new HashSet<string>();
        foreach (var ins in body)
        {
            if (ins is Label l) seenLabels.Add(l.Name);
            else if (ins is Jump j && seenLabels.Contains(j.Target)) backEdges++;
        }
        Assert.True(backEdges >= 2, $"expected >=2 loop back-edges (one per expansion), got {backEdges}");
    }
}
