using PyMCU.Common;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// `list(x)` / `tuple(x)` on a runtime heap list: a fresh GC object holding the
/// same elements. Unmodified adafruit_irremote writes both in decode_bits --
/// `input_pulses = tuple(pulses)` and `pulses = list(pulses)` -- and each
/// needs the copy to carry the element type so `y[i]`, `len(y)` and
/// `y.append()` on the result resolve like the source's.
/// </summary>
public class ListCopyCtorTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    [Fact]
    public void ListCopy_EmitsNewHeapObjectWithElementCopy()
    {
        var ir = Gen(
            "def f(src: list[uint16]) -> uint16:\n" +
            "    y = list(src)\n" +
            "    return y[0]\n" +
            "def main():\n" +
            "    a: list[uint16] = [1, 2]\n" +
            "    f(a)\n");
        Assert.Contains(ir.Functions, f => f.Body.Any(i => i is GcAlloc));
    }

    [Fact]
    public void TupleCopy_EmitsNewHeapObjectWithElementCopy()
    {
        var ir = Gen(
            "def f(src: list[uint16]) -> uint16:\n" +
            "    y = tuple(src)\n" +
            "    return y[0]\n" +
            "def main():\n" +
            "    a: list[uint16] = [1, 2]\n" +
            "    f(a)\n");
        Assert.Contains(ir.Functions, f => f.Body.Any(i => i is GcAlloc));
    }

    [Fact]
    public void ListCopy_ResultKeepsElementTypeForAppend()
    {
        var ir = Gen(
            "def f(src: list[uint16]) -> uint16:\n" +
            "    y = list(src)\n" +
            "    y.append(9)\n" +
            "    return y[0]\n" +
            "def main():\n" +
            "    a: list[uint16] = [1, 2]\n" +
            "    f(a)\n");
        // The append store must be 2 bytes -- the copy carried uint16 through.
        Assert.Contains(ir.Functions,
            f => f.Body.Any(i => i is StoreIndirect { Elem: DataType.UINT16 }));
    }

    [Fact]
    public void ListCopy_OfStillPendingList_Refuses()
    {
        // `x = []` promoted but no append has taught it an element type yet --
        // there is no element width to size the copy with.
        Assert.Throws<CompilerError>(() => Gen(
            "def f() -> uint8:\n" +
            "    x = []\n" +
            "    y = list(x)\n" +
            "    x.append(3)\n" +
            "    return 0\n" +
            "def main():\n" +
            "    f()\n"));
    }

    [Fact]
    public void ListCopy_OfScalar_Refuses()
    {
        Assert.Throws<CompilerError>(() => Gen(
            "def f(v: uint8) -> uint8:\n" +
            "    y = list(v)\n" +
            "    return 0\n" +
            "def main():\n" +
            "    f(1)\n"));
    }
}
