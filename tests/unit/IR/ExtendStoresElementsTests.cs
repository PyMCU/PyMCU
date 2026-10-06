using FluentAssertions;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// `xs.extend([1, 2, 3])` reported the grown length but stored nothing: the handler
/// bumped the element count and zero-filled, so `xs[k]` read 0 for every appended
/// slot. `xs += [1, 2, 3]` on the same receiver already routed each element through
/// the canonical indexed store; `.extend` now does the same, so the values a program
/// extends with are the values it reads back.
///
/// The second case is the const-sequence twin: `xs = [9, 9]; xs.extend([1, 2])`
/// wrote through correctly once the elements were stored, but `xs[2]` still folded
/// against the literal's size 2 because the write scan never counted a mutating
/// method's receiver. It does now, so the read reaches the grown storage.
/// </summary>
public class ExtendStoresElementsTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    private static List<Instruction> AllInstructions(ProgramIR ir) =>
        ir.Functions.SelectMany(f => f.Body).ToList();

    private static bool StoresConstant(Instruction i, int v) =>
        i is Copy { Src: Constant c } && c.Value == v
        || i is ArrayStore { Src: Constant s } && s.Value == v;

    [Fact]
    public void ExtendOnAnEmptyList_StoresTheGivenElements()
    {
        var ir = Gen(
            "xs = []\n" +
            "xs.extend([1, 2, 3])\n" +
            "a = xs[0]\n" +
            "b = xs[2]\n" +
            "n = len(xs)\n");

        var body = AllInstructions(ir);
        // Zero-fill would put Constant(0) into every slot; the grown elements must
        // carry the literal values instead.
        foreach (var v in new[] { 1, 2, 3 })
            Assert.Contains(body, i => StoresConstant(i, v));
    }

    [Fact]
    public void ExtendOnALiteralSequence_GrowsPastTheLiteralSize()
    {
        // Without the scan counting `.extend` as a write on `xs`, the const-fold
        // answered `xs[2]` from the literal's two elements: IndexError at compile
        // time on a program CPython runs.
        var ir = Gen(
            "xs = [9, 9]\n" +
            "xs.extend([1, 2])\n" +
            "a = xs[2]\n" +
            "b = xs[3]\n" +
            "n = len(xs)\n");

        var body = AllInstructions(ir);
        Assert.Contains(body, i => StoresConstant(i, 1));
        Assert.Contains(body, i => StoresConstant(i, 2));
    }

    [Fact]
    public void ExtendEvaluatesTheArgumentBeforeGrowingTheReceiver()
    {
        // `xs.extend([len(xs)])`: CPython reads len(xs) = 1 before the receiver
        // mutates, so xs[1] is 1. Lowering the argument after the size bump
        // reads the grown length and stores 2 instead.
        var ir = Gen(
            "xs = [9]\n" +
            "xs.extend([len(xs)])\n" +
            "a = xs[1]\n");

        var body = AllInstructions(ir);
        Assert.Contains(body, i =>
            (i is Copy cp1 && cp1.Dst is Variable { Name: "main.xs__1" } && cp1.Src is Constant { Value: 1 })
            || (i is ArrayStore s1 && s1.ArrayName == "main.xs" && s1.Src is Constant { Value: 1 }));
        Assert.DoesNotContain(body, i =>
            (i is Copy cp2 && cp2.Dst is Variable { Name: "main.xs__1" } && cp2.Src is Constant { Value: 2 })
            || (i is ArrayStore s2 && s2.ArrayName == "main.xs" && s2.Index is Constant { Value: 1 } && s2.Src is Constant { Value: 2 }));
    }

    [Fact]
    public void ExtendFromAnotherSequence_StoresEachElementIntoTheReceiver()
    {
        var ir = Gen(
            "src = [7, 8]\n" +
            "xs = []\n" +
            "xs.extend(src)\n" +
            "a = xs[0]\n" +
            "b = xs[1]\n");

        // 7 and 8 appear in the IR anyway -- src's own declaration stores them. The
        // extend is only fixed when a store lands on XS's element slots; at the buggy
        // revision the grown elements were zero-filled and `xs__0` never saw a write.
        var body = AllInstructions(ir);
        Assert.Contains(body, i =>
            i is Copy cp && cp.Dst is Variable { Name: "main.xs__0" });
        Assert.Contains(body, i =>
            i is Copy cp && cp.Dst is Variable { Name: "main.xs__1" });
    }

    [Fact]
    public void ExtendWhoseArgumentExtendsTheReceiver_AppendsPastTheNestedGrowth()
    {
        // `xs.extend([grow()])` where grow() itself extends xs: CPython evaluates
        // the argument completely before the receiver grows, so [1] becomes
        // [1, 7, 8] -- the nested claim takes index 1 and the outer element lands
        // at index 2. Reading the logical end before the argument ran put it at
        // index 1 instead, overwriting grow's 7, and `xs[1]` read 8.
        var ir = Gen(
            "xs = [1]\n\n" +
            "def grow():\n" +
            "    xs.extend([7])\n" +
            "    return 8\n\n" +
            "xs.extend([grow()])\n" +
            "n = len(xs)\n" +
            "a = xs[1]\n");

        var body = AllInstructions(ir);
        // grow's own extend claims index 1 with its 7, exactly once -- at the
        // buggy revision the outer store landed on top of it (a second write to
        // xs__1 carrying grow's pinned result).
        Assert.Single(body, i => i is Copy c
            && c.Dst is Variable { Name: "main.xs__1" });
        Assert.Contains(body, i => StoresConstant(i, 7));
        // The outer element lands at index 2 -- before the fix there was no
        // store to xs__2 at all.
        Assert.Contains(body, i => i is Copy c2
            && c2.Dst is Variable { Name: "main.xs__2" });
    }
}
