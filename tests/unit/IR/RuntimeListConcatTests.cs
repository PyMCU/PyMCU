using PyMCU.Common;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// `a + b` on two runtime heap lists allocates a fresh list holding both
/// payloads -- CPython's list concat. Without it the two pointers fell through
/// to the numeric add and the result was a meaningless sum of addresses.
/// Unmodified adafruit_irremote iterates `(even_bins + odd_bins)` inside a
/// comprehension in decode_bits.
/// </summary>
public class RuntimeListConcatTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    [Fact]
    public void ListPlusList_EmitsHeapConcat()
    {
        var ir = Gen(
            "def f(a: list[uint16], b: list[uint16]) -> uint16:\n" +
            "    c = a + b\n" +
            "    return c[0]\n" +
            "def main():\n" +
            "    x: list[uint16] = [1, 2]\n" +
            "    y: list[uint16] = [3, 4]\n" +
            "    f(x, y)\n");
        var bodies = ir.Functions.SelectMany(f => f.Body).ToList();
        // one alloc for x, one for y, one for the concat result
        Assert.True(bodies.OfType<GcAlloc>().Count() >= 3);
    }

    [Fact]
    public void Concat_ResultIsAList_LenAndIndexResolve()
    {
        // `len(c)` and `c[i]` on the concatenated name only compile when the
        // result is registered as a runtime list with an element type.
        var ir = Gen(
            "def f(a: list[uint16], b: list[uint16]) -> uint16:\n" +
            "    c = a + b\n" +
            "    n = len(c)\n" +
            "    return c[n - 1]\n" +
            "def main():\n" +
            "    x: list[uint16] = [1, 2]\n" +
            "    y: list[uint16] = [3, 4]\n" +
            "    f(x, y)\n");
        Assert.NotEmpty(ir.Functions);
    }

    [Fact]
    public void Concat_KeepsElementType_ForNestedLists()
    {
        var ir = Gen(
            "def f(a: list, b: list) -> uint16:\n" +
            "    c = a + b\n" +
            "    return c[0][0]\n" +
            "def main():\n" +
            "    x: list[list[uint16]] = [[1, 0]]\n" +
            "    y: list[list[uint16]] = [[2, 0]]\n" +
            "    f(x, y)\n");
        var bodies = ir.Functions.SelectMany(f => f.Body).ToList();
        Assert.Contains(bodies, i => i is GcAlloc g && g.Refs);
    }

    [Fact]
    public void ListPlusScalar_Refuses()
    {
        var ex = Assert.Throws<TypeError>(() => Gen(
            "def f(a: list[uint16], b: uint16) -> uint16:\n" +
            "    c = a + b\n" +
            "    return 0\n" +
            "def main():\n" +
            "    x: list[uint16] = [1, 2]\n" +
            "    f(x, 5)\n"));
        Assert.Contains("concatenate", ex.Message);
    }
}
