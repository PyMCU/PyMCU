using FluentAssertions;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// `x = f()` where f inlined `return <its local buffer>` binds x as another name
/// for that storage, so `x[i]` reads the callee's element slots. Through a SECOND
/// @inline hop -- `g` whose body is `return f(i)` -- the call's value arrived as
/// the outer expansion's ResultTemp, a Temporary aliased to the buffer rather than
/// the buffer Variable itself, and the alias check that only looked at Variables
/// missed it. `x` bound a scalar instead, and `x[0]` lowered to a BitCheck on a
/// slot the expansion never wrote: adafruit_pixelbuf's `px[0]` chain
/// (__getitem__ -> _getitem -> `return value`) printed 0 for every pixel.
/// </summary>
public class InlineNestedBufferReturnTests
{
    private static ProgramIR Gen(string src, bool optimize = true)
    {
        var ir = new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });
        return optimize ? Optimizer.Optimize(ir) : ir;
    }

    private const string Program =
        "from pymcu.types import uint8, inline\n\n" +
        "xs = bytearray(6)\n" +
        "xs[0] = 10\n\n" +
        "@inline\n" +
        "def get3(i: uint8):\n" +
        "    v = [xs[i], xs[i + 1], xs[i + 2]]\n" +
        "    return v\n\n" +
        "@inline\n" +
        "def outer(i: uint8):\n" +
        "    return get3(i)\n\n" +
        "r = outer(0)\n" +
        "y = r[0]\n" +
        "z = r[1]\n";

    [Fact]
    public void ABufferReturnedThroughTwoInlineHopsIsIndexedNotBitChecked()
    {
        var ir = Gen(Program);
        var main = ir.Functions.Single(f => f.Name == "main");

        Assert.DoesNotContain(main.Body,
            i => i is BitCheck b && b.Source is Variable sv && sv.Name == "r");
        Assert.Contains(main.Body,
            i => i is Copy { Src: Variable sv } && sv.Name.EndsWith("get3.v__0"));
        Assert.Contains(main.Body,
            i => i is Copy { Src: Variable sv2 } && sv2.Name.EndsWith("get3.v__1"));
    }

    [Fact]
    public void TheReturnedBufferAliasSurvivesToASecondCallSite()
    {
        // Each call refills the same expansion slot; a second `s = outer(1)`
        // must bind s to it too, or s[k] reads whatever the scalar copy left.
        var ir = Gen(Program + "s = outer(1)\n" + "w = s[0]\n");
        var main = ir.Functions.Single(f => f.Name == "main");

        Assert.DoesNotContain(main.Body,
            i => i is BitCheck b && b.Source is Variable sv && sv.Name == "s");
        Assert.Equal(2, main.Body.Count(
            i => i is Copy { Src: Variable sv } && sv.Name.EndsWith("get3.v__0")));
    }
}
