using PyMCU.Common;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// A slice of a runtime heap list materializes a fresh GC object holding every
/// step-th element of [start, stop). Unmodified adafruit_irremote writes
/// `pulses[1:pulses_end:2]` and `pulses[2:pulses_end:2]` in decode_bits -- a
/// runtime stop, a literal step, and a result that keeps the element type so
/// `e[i]`, `len(e)` and `e.append()` resolve like the source's.
/// </summary>
public class RuntimeListSliceTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    [Fact]
    public void StridedSlice_EmitsHeapCopy()
    {
        var ir = Gen(
            "def f(src: list[uint16]) -> uint16:\n" +
            "    e = src[1:9:2]\n" +
            "    return e[0]\n" +
            "def main():\n" +
            "    a: list[uint16] = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10]\n" +
            "    f(a)\n");
        Assert.Contains(ir.Functions, f => f.Body.Any(i => i is GcAlloc));
    }

    [Fact]
    public void StridedSlice_RuntimeStop_EmitsHeapCopy()
    {
        var ir = Gen(
            "def f(src: list[uint16], end: uint16) -> uint16:\n" +
            "    e = src[1:end:2]\n" +
            "    return e[0]\n" +
            "def main():\n" +
            "    a: list[uint16] = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10]\n" +
            "    f(a, 7)\n");
        Assert.Contains(ir.Functions, f => f.Body.Any(i => i is GcAlloc));
    }

    [Fact]
    public void Slice_ResultKeepsElementTypeForAppend()
    {
        var ir = Gen(
            "def f(src: list[uint16]) -> uint16:\n" +
            "    e = src[0:8:2]\n" +
            "    e.append(9)\n" +
            "    return e[0]\n" +
            "def main():\n" +
            "    a: list[uint16] = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10]\n" +
            "    f(a)\n");
        Assert.Contains(ir.Functions,
            f => f.Body.Any(i => i is StoreIndirect { Elem: DataType.UINT16 }));
    }

    [Fact]
    public void Slice_OfStillPendingList_Refuses()
    {
        Assert.Throws<CompilerError>(() => Gen(
            "def f() -> uint8:\n" +
            "    x = []\n" +
            "    y = x[1:5:2]\n" +
            "    x.append(3)\n" +
            "    return 0\n" +
            "def main():\n" +
            "    f()\n"));
    }

    [Fact]
    public void Slice_RuntimeStep_Refuses()
    {
        // A step read out of a list is genuinely runtime -- call-site folding
        // cannot see it, and `src[0]` may be zero at run time, so the slice
        // reports rather than emit a division by it.
        Assert.Throws<CompilerError>(() => Gen(
            "def f(src: list[uint16]) -> uint16:\n" +
            "    e = src[1:9:src[0]]\n" +
            "    return 0\n" +
            "def main():\n" +
            "    a: list[uint16] = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10]\n" +
            "    f(a)\n"));
    }

    [Fact]
    public void Slice_NegativeStep_Refuses()
    {
        Assert.Throws<CompilerError>(() => Gen(
            "def f(src: list[uint16]) -> uint16:\n" +
            "    e = src[9:1:-1]\n" +
            "    return 0\n" +
            "def main():\n" +
            "    a: list[uint16] = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10]\n" +
            "    f(a)\n"));
    }
}
