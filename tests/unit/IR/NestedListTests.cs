using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// `list[list[T]]`: the outer list's elements are GC_REF pointers to inner
/// lists. Three separate miscompiles made the shape unusable:
///
///   * `StringToDataType("list[uint16]")` answered UNKNOWN, so an element type
///     that is itself a list resolved to a one-byte scalar -- `bins[b]` loaded
///     a single payload byte instead of the inner list's heap pointer.
///   * `bins[b][0]` had no lowering: the outer subscript fell through to the
///     register-bit path and read a bit of the pointer's low byte. On the
///     emulator `bins[0][0]` printed 1 where the bin held [562, 0]
///     (adafruit_irremote's bin_data produced one bin per pulse, then the
///     outlier flood exhausted the heap).
///   * The GC object header had no way to say "this payload holds pointers",
///     so collection never marked inner lists and never rewrote the outer
///     list's slots when an inner list moved.
///
/// These tests pin the IR shape; the runtime half lives in pymcu-avr's
/// gc_runtime.S (bit6 = ref-bearing payload).
/// </summary>
public class NestedListTests
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
    public void ListAnnotationResolvesToGcRef()
    {
        // `list[uint16]` as an element type (inside list[list[uint16]]) is a
        // managed pointer, not an UNKNOWN scalar.
        Assert.Equal(DataType.GC_REF, DataTypeExtensions.StringToDataType("list[uint16]"));
        Assert.Equal(DataType.GC_REF, DataTypeExtensions.StringToDataType("list[list[uint8]]"));
    }

    [Fact]
    public void NestedSubscriptReadsThroughTwoHeapLoads()
    {
        var ir = Gen(
            "from pymcu.types import uint16\n\n" +
            "first: list[uint16] = [562, 0]\n" +
            "bins: list[list[uint16]] = [first]\n" +
            "v: uint16 = bins[0][0]\n");
        var main = ir.Functions.Single(f => f.Name == "main");

        // The inner subscript loads the inner list pointer (GC_REF) out of the
        // outer payload, then the element load reads a uint16 out of it.
        var loads = main.Body.OfType<LoadIndirect>().ToList();
        Assert.Contains(loads, l => DstType(l.Dst) == DataType.GC_REF);
        Assert.Contains(loads, l => DstType(l.Dst) == DataType.UINT16);

        // The element lands in `v` as a real uint16, not a bit test.
        Assert.DoesNotContain(main.Body, i => i is BitCheck);
    }

    [Fact]
    public void NestedSubscriptStoreWritesTheInnerList()
    {
        var ir = Gen(
            "from pymcu.types import uint16\n\n" +
            "first: list[uint16] = [562, 0]\n" +
            "bins: list[list[uint16]] = [first]\n" +
            "bins[0][1] = 99\n");
        var main = ir.Functions.Single(f => f.Name == "main");

        // A GC_REF load (inner list pointer) precedes a uint16 indirect store.
        var kinds = main.Body.ToList();
        int refLoad = kinds.FindIndex(i => i is LoadIndirect l && DstType(l.Dst) == DataType.GC_REF);
        Assert.True(refLoad >= 0);
        Assert.Contains(kinds.Skip(refLoad + 1),
            i => i is StoreIndirect s && s.Elem == DataType.UINT16);
        Assert.DoesNotContain(kinds, i => i is BitWrite);
    }

    [Fact]
    public void ForOverNestedListBindsAnInnerList()
    {
        var ir = Gen(
            "from pymcu.types import uint16\n\n" +
            "first: list[uint16] = [562, 0]\n" +
            "bins: list[list[uint16]] = [first]\n\n" +
            "total: uint16 = 0\n" +
            "for kb in bins:\n" +
            "    total = total + kb[1]\n");
        var main = ir.Functions.Single(f => f.Name == "main");

        // The loop variable is the inner list pointer, and kb[1] loads a
        // uint16 element off it -- never a bit check.
        Assert.Contains(main.Body, i => i is LoadIndirect l && DstType(l.Dst) == DataType.GC_REF);
        Assert.DoesNotContain(main.Body, i => i is BitCheck);
    }

    [Fact]
    public void OuterListAllocCarriesTheRefsFlag()
    {
        var ir = Gen(
            "from pymcu.types import uint16\n\n" +
            "first: list[uint16] = [562, 0]\n" +
            "bins: list[list[uint16]] = [first]\n" +
            "plain: list[uint16] = [1, 2]\n");
        var main = ir.Functions.Single(f => f.Name == "main");

        var allocs = main.Body.OfType<GcAlloc>().ToList();
        // first + bins + plain = 3 allocations; exactly one is ref-bearing.
        Assert.Equal(3, allocs.Count);
        Assert.Single(allocs, a => a.Refs);
    }

    [Fact]
    public void AugmentedNestedStoreWritesTheInnerList()
    {
        // `bins[b][1] += v` on a list[list[T]]: the read resolved through the
        // two-level index but the store-back had no nested path and fell to a
        // bit write on the loaded pointer.
        var ir = Gen(
            "from pymcu.types import uint16\n\n" +
            "def bump(bins: list[list[uint16]], b: uint8) -> None:\n" +
            "    bins[b][1] += 1\n\n" +
            "first: list[uint16] = [562, 0]\n" +
            "bins: list[list[uint16]] = [first]\n" +
            "bump(bins, 0)\n");
        var bodies = ir.Functions.SelectMany(f => f.Body).ToList();

        Assert.Contains(bodies,
            i => i is StoreIndirect s && s.Elem == DataType.UINT16);
        Assert.DoesNotContain(bodies, i => i is BitWrite || i is BitCheck);
    }

    [Fact]
    public void NestedListGrowAllocCarriesTheRefsFlag()
    {
        var ir = Gen(
            "from pymcu.types import uint16\n\n" +
            "def add(bins: list[list[uint16]], inner: list[uint16]) -> None:\n" +
            "    bins.append(inner)\n\n" +
            "first: list[uint16] = [562, 0]\n" +
            "bins: list[list[uint16]] = [first]\n" +
            "add(bins, first)\n");
        var main = ir.Functions.Single(f => f.Name == "main");

        // The append's grow-path allocation must flag the new buffer too.
        Assert.Contains(main.Body, i => i is GcAlloc a && a.Refs);
    }

    // `row = cells[y]` binds the name to the inner list itself -- CPython alias
    // semantics, so `row[k] = v` writes through to `cells[y][k]`.
    [Fact]
    public void BoundRowNamesItsOwnSlot()
    {
        var ir = Gen(
            "from pymcu.types import uint8\n\n" +
            "cells: list[list[uint8]] = [[0, 0], [0, 0]]\n" +
            "y: uint8 = 1\n" +
            "row = cells[y]\n" +
            "row[0] = 9\n" +
            "q = row[0]\n");
        var main = ir.Functions.Single(f => f.Name == "main");

        // `row` receives the pointer in its own slot, and `row[k]` computes
        // element addresses from `row`. Before the fix the name aliased the
        // inner load's scratch temp, copy propagation folded `row = tmp_N`
        // into the load's dst, and `row[k]` then read a `tmp_N` slot nothing
        // ever wrote -- the row answered 0 whatever it held.
        Assert.Contains(main.Body,
            i => i is Binary b && b.Src1 is Variable { Name: "row" });
        Assert.DoesNotContain(main.Body,
            i => i is Binary b && b.Src1 is Variable { Name: { } n } && n.StartsWith("tmp_"));
        Assert.Contains(main.Body, i => i is StoreIndirect s && s.Elem == DataType.UINT8);
    }

    [Fact]
    public void BoundRowInsideAFunctionNamesItsOwnSlot()
    {
        var ir = Gen(
            "from pymcu.types import uint8\n\n" +
            "cells: list[list[uint8]] = [[0, 0], [7, 7]]\n\n" +
            "def f(y: uint8) -> uint8:\n" +
            "    row = cells[y]\n" +
            "    row[1] = 42\n" +
            "    return row[0]\n\n" +
            "q = f(1)\n");
        var fBody = ir.Functions.Single(f => f.Name == "f").Body;

        // Same binding inside a callee's frame: `f.row` is the list var.
        Assert.Contains(fBody,
            i => i is Binary b && b.Src1 is Variable { Name: "f.row" });
        Assert.DoesNotContain(fBody,
            i => i is Binary b && b.Src1 is Variable { Name: { } n } && n.StartsWith("tmp_"));
    }
}
