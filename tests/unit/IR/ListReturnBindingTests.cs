using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// `x = f()` where `f` returns `list[T]`: the result temp is GC_REF, and inside a
/// function the assigned name gets its own `listVarElemTypes` entry so `len(x)`,
/// `x[i]` and `for v in x` resolve it. At module level the name went through the
/// `mutableGlobals` branch instead, which widened the type but never registered
/// the element type -- `len(pulses)` refused "must be a fixed-size array or list
/// literal" even though `pulses` held a real heap list (adafruit_irremote's
/// `pulses = decoder.read_pulses(pulsein)` shape).
/// </summary>
public class ListReturnBindingTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    private const string Inner =
        "def inner() -> list[uint16]:\n" +
        "    xs: list[uint16] = []\n" +
        "    xs.append(1)\n" +
        "    return xs\n\n";

    [Fact]
    public void AListReturnedIntoAFunctionLocal_KeepsItsListness()
    {
        var ir = Gen(Inner +
            "def outer() -> uint8:\n" +
            "    pulses = inner()\n" +
            "    return len(pulses)\n" +
            "def main():\n" +
            "    n = outer()\n");
        var outer = ir.Functions.Single(f => f.Name == "outer");
        Assert.Contains(outer.Body, i => i is LoadIndirect li
            && li.SrcPtr is Variable v && v.Name == "outer.pulses" && v.Type == DataType.GC_REF);
    }

    [Fact]
    public void AListReturnedIntoAModuleName_KeepsItsListness()
    {
        var ir = Gen(Inner +
            "pulses = inner()\n" +
            "n = len(pulses)\n");
        Assert.Contains(ir.Globals, g => g.Name == "pulses" && g.Type == DataType.GC_REF);
        var main = ir.Functions.Single(f => f.Name == "main");
        Assert.Contains(main.Body, i => i is LoadIndirect li
            && li.SrcPtr is Variable v && v.Name == "pulses" && v.Type == DataType.GC_REF);
    }

    [Fact]
    public void AListReturnedIntoAModuleName_IteratesAndSubscripts()
        => Assert.NotNull(Gen(Inner +
            "pulses = inner()\n" +
            "first = pulses[0]\n" +
            "total: uint16 = 0\n" +
            "for p in pulses:\n" +
            "    total = total + p\n"));

    // An UNANNOTATED callee returning a list[T] local carries no declared text --
    // the element type is recorded when its `return <list var>` emits instead.
    // The `while True` keeps the callee from inlining, so the caller sees a real
    // call result rather than an expansion's variable.
    [Fact]
    public void AnUnannotatedOutlinedListReturn_BindsTheCallerName()
    {
        var ir = Gen(
            "def inner():\n" +
            "    xs: list[uint16] = []\n" +
            "    while True:\n" +
            "        xs.append(1)\n" +
            "        return xs\n" +
            "def outer() -> uint8:\n" +
            "    pulses = inner()\n" +
            "    return len(pulses)\n" +
            "def main():\n" +
            "    n = outer()\n");
        var outer = ir.Functions.Single(f => f.Name == "outer");
        Assert.Contains(outer.Body, i => i is LoadIndirect li
            && li.SrcPtr is Variable v && v.Name == "outer.pulses" && v.Type == DataType.GC_REF);
    }

    // `n = len(p)` right after `p = inner() -> list[T]`: the len() call must not
    // inherit the earlier call's return text -- n holds a count, not a list.
    [Fact]
    public void ALenAfterAListReturn_StaysAScalar()
    {
        var ir = Gen(Inner +
            "def outer() -> uint8:\n" +
            "    p = inner()\n" +
            "    n = len(p)\n" +
            "    return n + 0\n" +
            "def main():\n" +
            "    x = outer()\n");
        var outer = ir.Functions.Single(f => f.Name == "outer");
        Assert.DoesNotContain(outer.Body, i => i is Copy { Dst: Variable v }
            && v.Name == "outer.n" && v.Type == DataType.GC_REF);
    }

    // `return g()` where g hands back a list: the element type chains through the
    // caller's own return, so `x = f()` registers x even when f only forwards.
    [Fact]
    public void AForwardedListReturn_BindsTheCallerName()
    {
        var ir = Gen(Inner +
            "def forward():\n" +
            "    while True:\n" +
            "        return inner()\n" +
            "def outer() -> uint8:\n" +
            "    pulses = forward()\n" +
            "    return len(pulses)\n" +
            "def main():\n" +
            "    n = outer()\n");
        var outer = ir.Functions.Single(f => f.Name == "outer");
        Assert.Contains(outer.Body, i => i is LoadIndirect li
            && li.SrcPtr is Variable v && v.Name == "outer.pulses" && v.Type == DataType.GC_REF);
    }
}
