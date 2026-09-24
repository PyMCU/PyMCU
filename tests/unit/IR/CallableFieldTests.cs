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

        // The dispatch lands inside the shared bound body when `go` outlines.
        ir.Functions.SelectMany(f => f.Body)
            .Any(i => i is Call c && c.FunctionName == "tick").Should().BeTrue(
            "self.f() dispatches to the function the field was bound to");
        ir.Functions.SelectMany(f => f.Body)
            .Any(i => i is Call c && c.FunctionName.EndsWith("_f")).Should().BeFalse(
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

        // Each instance's shared bound body dispatches to its own binding.
        ir.Functions.SelectMany(f => f.Body)
            .Any(i => i is Call c && c.FunctionName == "a").Should().BeTrue();
        ir.Functions.SelectMany(f => f.Body)
            .Any(i => i is Call c && c.FunctionName == "b").Should().BeTrue();
    }

    [Fact]
    public void AValueBoundParam_IsNotAFunctionField()
    {
        // `self._n = number_of_shift_registers` in adafruit_74hc595's ctor: the
        // parameter shares its name with the class's own property getter, so
        // the callable-field probe resolved it as a function reference and the
        // int field was never stored -- later reads loaded an unwritten
        // variable and returned 0.
        var ir = Gen(
            "class S:\n" +
            "    def __init__(self, n: int = 1) -> None:\n" +
            "        self._n = n\n" +
            "    def n(self) -> int:\n" +
            "        return self._n\n\n" +
            "s = S(5)\n" +
            "x = s.n()\n");

        var main = ir.Functions.Single(f => f.Name == "main");
        main.Body.Any(i => i is Call c && c.FunctionName == "n").Should().BeFalse(
            "the int param must not be stored as a callable field");
    }

    [Fact]
    public void AFieldReadAfterCtorWrite_FoldsTheStoredValue()
    {
        // Same shape as above through a property: the getter's read must see
        // the constant the ctor stored, not a runtime variable nothing wrote.
        var ir = Gen(
            "class S:\n" +
            "    def __init__(self, number_of_shift_registers: int = 1) -> None:\n" +
            "        self._number_of_shift_registers = number_of_shift_registers\n" +
            "    @property\n" +
            "    def number_of_shift_registers(self) -> int:\n" +
            "        return self._number_of_shift_registers\n\n" +
            "s = S()\n" +
            "x = s.number_of_shift_registers\n");

        var main = ir.Functions.Single(f => f.Name == "main");
        main.Body.Any(i => i is Copy cp
                && cp.Src is Variable v && v.Name == "s__number_of_shift_registers")
            .Should().BeFalse("the stored constant folds every read; a var load means 0");
    }

    [Fact]
    public void AValueBoundLocal_IsNotAFunctionField()
    {
        // `auto_write = self._auto_write` then `self._auto_write = auto_write`
        // in adafruit_ht16k33's Seg14x4._number: the local shares its name with
        // the class's own auto_write property setter, so the restore's
        // callable-field probe resolved the setter and swallowed the store --
        // the field stayed False and every `if self._auto_write: self.show()`
        // skipped its flush. boundNames marks the local a value binding, which
        // must shadow the same-named setter.
        var ir = Gen(
            "class S:\n" +
            "    def __init__(self) -> None:\n" +
            "        self._auto_write = True\n" +
            "    @property\n" +
            "    def auto_write(self) -> bool:\n" +
            "        return self._auto_write\n" +
            "    @auto_write.setter\n" +
            "    def auto_write(self, v: bool) -> None:\n" +
            "        self._auto_write = v\n" +
            "    def m(self) -> None:\n" +
            "        auto_write = self._auto_write\n" +
            "        self._auto_write = False\n" +
            "        self._auto_write = auto_write\n\n" +
            "s = S()\n" +
            "s.m()\n" +
            "x = s.auto_write\n", optimize: false);

        ir.Functions.SelectMany(f => f.Body).Any(i => i is Copy cp
                && cp.Dst is Variable v && v.Name.Contains("__auto_write")
                && cp.Src is Variable s && s.Name.Contains(".auto_write"))
            .Should().BeTrue("the restore must emit a real store of the saved local");
    }
}
