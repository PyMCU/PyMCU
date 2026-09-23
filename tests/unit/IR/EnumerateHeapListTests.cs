using PyMCU.Common;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// `for i, v in enumerate(lst)` on a runtime heap list: the enumerate dispatch
/// answered compile-time sequences, strings, splits, ranges and fixed arrays,
/// but a heap list has no entry in arraySizes -- the loop fell through to the
/// builtin refusal. It lowers as a counter loop whose bound loads the object
/// header's count byte, the same shape `for v in lst` runs with the index name
/// alongside. adafruit_irremote's bin_data writes
/// `for _, pulse in enumerate(pulses)` and `for b, pulse_bin in enumerate(bins)`.
/// </summary>
public class EnumerateHeapListTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    [Fact]
    public void Enumerate_HeapList_EmitsCounterLoop()
    {
        var ir = Gen(
            "def f(p: list[uint16]) -> uint16:\n" +
            "    s: uint16 = 0\n" +
            "    for i, v in enumerate(p):\n" +
            "        s = s + v\n" +
            "    return s\n" +
            "def main():\n" +
            "    x: list[uint16] = [9, 8]\n" +
            "    f(x)\n");

        var bodies = ir.Functions.SelectMany(f => f.Body).ToList();
        // The bound is a header load; the element read is a payload load.
        Assert.Contains(bodies, i => i is LoadIndirect);
        // A real loop: a jump back to a label, driven by the index compare.
        Assert.Contains(bodies, i => i is Jump);
        Assert.DoesNotContain(bodies, i => i is BitCheck);
    }

    [Fact]
    public void Enumerate_ListOfLists_BindsInnerList()
    {
        var ir = Gen(
            "def f(p: list[uint16]) -> uint16:\n" +
            "    bins: list[list[uint16]] = [[p[0], 0]]\n" +
            "    s: uint16 = 0\n" +
            "    for b, pb in enumerate(bins):\n" +
            "        s = s + pb[0]\n" +
            "    return s\n" +
            "def main():\n" +
            "    x: list[uint16] = [9, 8]\n" +
            "    f(x)\n");

        var bodies = ir.Functions.SelectMany(f => f.Body).ToList();
        // pb is GC_REF; pb[0] loads a uint16 element through it.
        Assert.Contains(bodies, i => i is LoadIndirect l &&
            (l.Dst is Temporary t && t.Type == DataType.GC_REF
             || l.Dst is Variable v && v.Type == DataType.GC_REF));
        Assert.DoesNotContain(bodies, i => i is BitCheck);
    }

    [Fact]
    public void Enumerate_PromotedEmptyList_AfterAppendWorks()
    {
        var ir = Gen(
            "def f(n: uint8) -> uint8:\n" +
            "    xs = []\n" +
            "    xs.append(n)\n" +
            "    s: uint8 = 0\n" +
            "    for i, v in enumerate(xs):\n" +
            "        s = s + v\n" +
            "    return s\n" +
            "def main():\n" +
            "    f(3)\n");
        Assert.Contains(ir.Functions.SelectMany(f => f.Body), i => i is GcAlloc);
    }

    [Fact]
    public void Enumerate_PendingElemRefuses()
    {
        // `x = []` promoted (a later append exists) but iterated BEFORE its
        // first append: the loop variable would have no element type. The
        // refusal names the fix.
        var ex = Assert.Throws<CompilerError>(() => Gen(
            "def f() -> uint8:\n" +
            "    xs = []\n" +
            "    for i, v in enumerate(xs):\n" +
            "        v = v\n" +
            "    xs.append(1)\n" +
            "    return 0\n" +
            "def main():\n" +
            "    f()\n"));
        Assert.Contains("element type", ex.Message);
    }
}
