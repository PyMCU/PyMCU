using FluentAssertions;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// A callable stored on an instance field -- `self.f = lambda: io.value` or
/// `self.f = io_or_predicate` -- is a compile-time binding keyed by the
/// field's flattened name, so `self.f()` later dispatches to whatever the
/// construction bound, per instance. adafruit_debouncer stores its probe this
/// way: a lambda over the pin for a DigitalInOut, the predicate itself for a
/// Callable.
/// </summary>
public class CallableFieldTests
{
    private static ProgramIR Gen(string src, bool optimize = true)
    {
        var ir = new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });
        return optimize ? Optimizer.Optimize(ir) : ir;
    }

    [Fact]
    public void AFunctionBoundToAField_CallsThatFunction()
    {
        var ir = Gen(
            "def tick() -> int:\n" +
            "    return 7\n\n" +
            "class D:\n" +
            "    def __init__(self, f) -> None:\n" +
            "        self.f = f\n" +
            "    def go(self) -> int:\n" +
            "        return self.f()\n\n" +
            "d = D(tick)\n" +
            "x = d.go()\n");

        var main = ir.Functions.Single(f => f.Name == "main");
        main.Body.Any(i => i is Call c && c.FunctionName == "tick").Should().BeTrue(
            "self.f() dispatches to the function the field was bound to");
        main.Body.Any(i => i is Call c && c.FunctionName.EndsWith("_f")).Should().BeFalse(
            "the field name is not a mangled method");
    }

    [Fact]
    public void ALambdaBoundToAField_ExpandsWithItsCaptures()
    {
        var ir = Gen(
            "class D:\n" +
            "    def __init__(self, n) -> None:\n" +
            "        self.f = lambda: n + 1\n" +
            "    def go(self) -> int:\n" +
            "        return self.f()\n\n" +
            "d = D(41)\n" +
            "x = d.go()\n");

        // The lambda's free name `n` captured the ctor argument -- 41 is a
        // compile-time constant there, so `n + 1` folds and x is 42.
        var main = ir.Functions.Single(f => f.Name == "main");
        main.Body.Any(i => i is Copy { Dst: Variable dv, Src: Constant sc }
            && dv.Name == "x" && sc.Value == 42).Should().BeTrue(
            "the lambda expanded at the call site with the captured parameter");
    }

    [Fact]
    public void RebindingAFieldToADifferentCallable_Refuses()
    {
        var act = () => Gen(
            "def a() -> int:\n    return 1\n\n" +
            "def b() -> int:\n    return 2\n\n" +
            "class D:\n" +
            "    def __init__(self) -> None:\n" +
            "        self.f = a\n" +
            "        self.f = b\n\n" +
            "d = D()\n");
        act.Should().Throw<Exception>().WithMessage("*dispatch table*");
    }

    [Fact]
    public void TwoInstances_KeepTheirOwnFieldBindings()
    {
        var ir = Gen(
            "def a() -> int:\n    return 1\n\n" +
            "def b() -> int:\n    return 2\n\n" +
            "class D:\n" +
            "    def __init__(self, f) -> None:\n" +
            "        self.f = f\n" +
            "    def go(self) -> int:\n" +
            "        return self.f()\n\n" +
            "d1 = D(a)\n" +
            "d2 = D(b)\n" +
            "x = d1.go()\n" +
            "y = d2.go()\n");

        var main = ir.Functions.Single(f => f.Name == "main");
        main.Body.Any(i => i is Call c && c.FunctionName == "a").Should().BeTrue();
        main.Body.Any(i => i is Call c && c.FunctionName == "b").Should().BeTrue();
    }
}
