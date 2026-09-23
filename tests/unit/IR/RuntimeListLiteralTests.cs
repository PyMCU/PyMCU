using PyMCU.Common;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// `bins = [[pulses[0], 0]]`: a list literal whose ELEMENTS are literals cannot
/// flatten into `name__k` slots -- a flattened slot answers `bins[0]` only at a
/// compile-time index, and adafruit_irremote's bin_data writes `bins[b][0]`
/// behind a run-time `b`. The literal materializes as a heap `list[list[T]]`:
/// each inner literal is its own gc_alloc'd object bound to a generated NAME
/// (a Temporary is not a GC root, so an unnamed pointer would not survive the
/// outer allocation's compaction), and the outer payload carries the
/// ref-bearing flag so the collector marks and fixes the inner pointers.
/// </summary>
public class RuntimeListLiteralTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    private static DataType? DstType(Val v) => v switch
    {
        Temporary t => t.Type,
        Variable vv => vv.Type,
        _ => null,
    };

    [Fact]
    public void NestedLiteral_MaterializesListOfLists()
    {
        var ir = Gen(
            "def f(p: list[uint16]) -> uint16:\n" +
            "    bins = [[p[0], 0]]\n" +
            "    return bins[0][0]\n" +
            "def main():\n" +
            "    x: list[uint16] = [9, 8]\n" +
            "    f(x)\n");

        var allocs = ir.Functions.SelectMany(f => f.Body).OfType<GcAlloc>().ToList();
        // f's outer + inner, plus main's x: 3 allocations, exactly one ref-bearing.
        Assert.Single(allocs, a => a.Refs);
        Assert.Contains(allocs, a => !a.Refs);

        // bins[0][0] is two indirect loads: inner pointer, then uint16 element.
        var loads = ir.Functions.SelectMany(f => f.Body).OfType<LoadIndirect>().ToList();
        Assert.Contains(loads, l => DstType(l.Dst) == DataType.GC_REF);
        Assert.Contains(loads, l => DstType(l.Dst) == DataType.UINT16);
    }

    [Fact]
    public void NestedLiteral_InnerPointerIsBoundToAName()
    {
        var ir = Gen(
            "def f(p: list[uint16]) -> uint16:\n" +
            "    bins = [[p[0], 0]]\n" +
            "    return bins[0][0]\n" +
            "def main():\n" +
            "    x: list[uint16] = [9, 8]\n" +
            "    f(x)\n");

        // Every inner-literal allocation result must be Copy'd into a named
        // Variable -- the names are what the GC-root scan sees; a Temporary
        // holding a heap pointer across the outer alloc would dangle.
        var bodies = ir.Functions.SelectMany(f => f.Body).ToList();
        var stores = bodies.OfType<StoreIndirect>()
            .Where(s => s.Elem == DataType.GC_REF).ToList();
        Assert.NotEmpty(stores);
        Assert.All(stores, s => Assert.IsType<Variable>(s.Src));
    }

    [Fact]
    public void NestedLiteral_AppendLiteralMaterializes()
    {
        var ir = Gen(
            "def f(p: list[uint16]) -> uint16:\n" +
            "    bins = [[p[0], 0]]\n" +
            "    bins.append([p[1], 1])\n" +
            "    return bins[1][1]\n" +
            "def main():\n" +
            "    x: list[uint16] = [9, 8]\n" +
            "    f(x)\n");

        var bodies = ir.Functions.SelectMany(f => f.Body).ToList();
        // Two inner literals + the outer + main's x + the append's grow-path
        // allocation (emitted whether or not it runs).
        Assert.Equal(5, bodies.OfType<GcAlloc>().Count());
        Assert.Contains(bodies, i => i is StoreIndirect s && s.Elem == DataType.GC_REF);
    }

    [Fact]
    public void NestedLiteral_RuntimeIndexStoreWritesInnerList()
    {
        var ir = Gen(
            "def f(p: list[uint16], b: uint8) -> uint16:\n" +
            "    bins = [[p[0], 0]]\n" +
            "    bins[b][0] = p[1]\n" +
            "    bins[b][1] += 1\n" +
            "    return bins[b][0]\n" +
            "def main():\n" +
            "    x: list[uint16] = [9, 8]\n" +
            "    f(x, 0)\n");

        var bodies = ir.Functions.SelectMany(f => f.Body).ToList();
        Assert.Contains(bodies, i => i is StoreIndirect s && s.Elem == DataType.UINT16);
        Assert.DoesNotContain(bodies, i => i is BitWrite || i is BitCheck);
    }

    [Fact]
    public void NestedLiteral_IterationBindsInnerList()
    {
        var ir = Gen(
            "def f(p: list[uint16]) -> uint16:\n" +
            "    bins = [[p[0], 0]]\n" +
            "    total: uint16 = 0\n" +
            "    for pb in bins:\n" +
            "        total = total + pb[1]\n" +
            "    return total\n" +
            "def main():\n" +
            "    x: list[uint16] = [9, 8]\n" +
            "    f(x)\n");

        var bodies = ir.Functions.SelectMany(f => f.Body).ToList();
        Assert.Contains(bodies, i => i is LoadIndirect l && DstType(l.Dst) == DataType.GC_REF);
        Assert.DoesNotContain(bodies, i => i is BitCheck);
    }

    [Fact]
    public void NestedLiteral_AnnotatedFormMatches()
    {
        var ir = Gen(
            "def f(p: list[uint16]) -> uint16:\n" +
            "    bins: list[list[uint16]] = [[p[0], 0]]\n" +
            "    bins.append([p[1], 1])\n" +
            "    return bins[1][0]\n" +
            "def main():\n" +
            "    x: list[uint16] = [9, 8]\n" +
            "    f(x)\n");

        var allocs = ir.Functions.SelectMany(f => f.Body).OfType<GcAlloc>().ToList();
        // The outer list and the append's grow-path alloc are both ref-bearing.
        Assert.Contains(allocs, a => a.Refs);
    }

    [Fact]
    public void NestedLiteral_BareAnnotationReturnCompiles()
    {
        // `-> list[list]`: adafruit_irremote's bin_data spells the bare inner
        // annotation; the returned name's registrations carry it.
        var ir = Gen(
            "def f(p: list) -> list[list]:\n" +
            "    bins = [[p[0], 0]]\n" +
            "    bins.append([p[1], 1])\n" +
            "    return bins\n" +
            "def main():\n" +
            "    x: list[uint16] = [9, 8]\n" +
            "    f(x)\n");
        Assert.Contains(ir.Functions.SelectMany(f => f.Body), i => i is GcAlloc a && a.Refs);
    }

    [Fact]
    public void NestedLiteral_MixedElementsRefuse()
    {
        var ex = Assert.Throws<CompilerError>(() => Gen(
            "def f() -> uint8:\n" +
            "    x = [[1], 2]\n" +
            "    return 0\n" +
            "def main():\n" +
            "    f()\n"));
        Assert.Contains("element type", ex.Message);
    }

    [Fact]
    public void NestedLiteral_ThreeLevelsRefuse()
    {
        var ex = Assert.Throws<CompilerError>(() => Gen(
            "def f() -> uint8:\n" +
            "    x = [[[1]]]\n" +
            "    return 0\n" +
            "def main():\n" +
            "    f()\n"));
        Assert.Contains("element type", ex.Message);
    }
}
