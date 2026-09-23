using PyMCU.Common;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// `[expr for v in xs if cond]` over a runtime heap list materializes a fresh
/// heap list: capacity is the source length (the filter can only shrink it),
/// and each kept element is stored at the out-index. Unmodified
/// adafruit_irremote writes `outliers = [b[0] for b in (even_bins + odd_bins)
/// if b[1] == 1]` and `even_bins = [b for b in even_bins if b[1] > 1]` in
/// decode_bits.
/// </summary>
public class RuntimeListCompTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    [Fact]
    public void FilteredComp_EmitsHeapList()
    {
        var ir = Gen(
            "def f(src: list[uint16]) -> uint16:\n" +
            "    keep = [x for x in src if x > 3]\n" +
            "    return keep[0]\n" +
            "def main():\n" +
            "    a: list[uint16] = [1, 2, 3, 4, 5]\n" +
            "    f(a)\n");
        var bodies = ir.Functions.SelectMany(f => f.Body).ToList();
        Assert.True(bodies.OfType<GcAlloc>().Count() >= 2);
    }

    [Fact]
    public void MappedComp_EmitsHeapList()
    {
        var ir = Gen(
            "def f(src: list[uint16]) -> uint16:\n" +
            "    heads = [x + 1 for x in src]\n" +
            "    return heads[0]\n" +
            "def main():\n" +
            "    a: list[uint16] = [1, 2, 3]\n" +
            "    f(a)\n");
        var bodies = ir.Functions.SelectMany(f => f.Body).ToList();
        Assert.True(bodies.OfType<GcAlloc>().Count() >= 2);
    }

    [Fact]
    public void Comp_ResultIsAList_LenAndIndexResolve()
    {
        var ir = Gen(
            "def f(src: list[uint16]) -> uint16:\n" +
            "    keep = [x for x in src if x > 3]\n" +
            "    n = len(keep)\n" +
            "    return keep[n - 1]\n" +
            "def main():\n" +
            "    a: list[uint16] = [1, 2, 3, 4, 5]\n" +
            "    f(a)\n");
        Assert.NotEmpty(ir.Functions);
    }

    [Fact]
    public void Comp_OverConcat_Works()
    {
        // `(a + b)` as the iterable: the concat runs first, then the filter
        // loop reads the fresh list.
        var ir = Gen(
            "def f(a: list[uint16], b: list[uint16]) -> uint16:\n" +
            "    big = [x for x in (a + b) if x > 2]\n" +
            "    return big[0]\n" +
            "def main():\n" +
            "    x: list[uint16] = [1, 2]\n" +
            "    y: list[uint16] = [3, 4]\n" +
            "    f(x, y)\n");
        var bodies = ir.Functions.SelectMany(f => f.Body).ToList();
        Assert.True(bodies.OfType<GcAlloc>().Count() >= 3);
    }

    [Fact]
    public void Comp_OfLists_KeepsRefElements()
    {
        // `[b for b in bins if b[1] > 1]` -- the element expression is the loop
        // variable itself, a list pointer, so the result is list[list[uint16]]
        // and its payload is ref-bearing.
        var ir = Gen(
            "def f(bins: list) -> uint16:\n" +
            "    keep = [b for b in bins if b[1] > 1]\n" +
            "    return keep[0][0]\n" +
            "def main():\n" +
            "    x: list[list[uint16]] = [[1, 2], [3, 4]]\n" +
            "    f(x)\n");
        var bodies = ir.Functions.SelectMany(f => f.Body).ToList();
        Assert.Contains(bodies, i => i is GcAlloc g && g.Refs);
    }

    [Fact]
    public void Comp_InnerIndexFilter_Works()
    {
        // `b[1] == 1` -- the filter reads the inner list through the loop var.
        var ir = Gen(
            "def f(bins: list) -> uint16:\n" +
            "    ones = [b[0] for b in bins if b[1] == 1]\n" +
            "    return ones[0]\n" +
            "def main():\n" +
            "    x: list[list[uint16]] = [[1, 1], [3, 4]]\n" +
            "    f(x)\n");
        var bodies = ir.Functions.SelectMany(f => f.Body).ToList();
        Assert.True(bodies.OfType<GcAlloc>().Count() >= 3);
    }
}
