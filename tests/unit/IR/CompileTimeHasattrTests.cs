using FluentAssertions;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// `hasattr(obj, "name")` on a statically-known shape is compile-time
/// decidable: an instance's class fixes its member set, and a Callable-union
/// parameter bound to a function has no instance attributes. This is how
/// adafruit_debouncer's __init__ picks between a value-like IO
/// (`Union[ROValueIO, Callable[[], bool]]` -- a DigitalInOut satisfies
/// ROValueIO structurally) and a predicate: `if hasattr(io_or_predicate,
/// "value"): self.function = lambda: io_or_predicate.value else:
/// self.function = io_or_predicate`. Before the fold the library refused with
/// "hasattr is runtime reflection".
/// </summary>
public class CompileTimeHasattrTests
{
    private static ProgramIR Gen(string src, bool optimize = true)
    {
        var ir = new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });
        return optimize ? Optimizer.Optimize(ir) : ir;
    }

    private const string Shape =
        "class Pin:\n" +
        "    def __init__(self, v) -> None:\n" +
        "        self._v = v\n" +
        "    @property\n" +
        "    def value(self) -> bool:\n" +
        "        return self._v != 0\n\n" +
        "def pred() -> bool:\n" +
        "    return False\n\n" +
        "class D:\n" +
        "    def __init__(self, io) -> None:\n" +
        "        self.state = 0\n" +
        "        if hasattr(io, \"value\"):\n" +
        "            self.f = lambda: io.value\n" +
        "        else:\n" +
        "            self.f = io\n" +
        "    def read(self) -> int:\n" +
        "        if self.f():\n" +
        "            return 1\n" +
        "        return 0\n\n";

    [Fact]
    public void AnInstanceArgument_FoldsHasattrTrue_AndBindsTheLambdaField()
    {
        // Compiling at all proves the fold landed True and the lambda bound:
        // the False branch would store the Pin instance itself and `self.f()`
        // would refuse on a non-callable.
        var ir = Gen(Shape +
            "p = Pin(0)\n" +
            "d = D(p)\n" +
            "x = d.read()\n");

        var main = ir.Functions.Single(f => f.Name == "main");
        main.Body.Any(i => i is Copy { Dst: Variable dv } && dv.Name.EndsWith("_f"))
            .Should().BeFalse("self.f is a compile-time binding, not a stored scalar");
        main.Body.Any(i => i is Call c && c.FunctionName.EndsWith("_f")).Should().BeFalse(
            "the field name is not a mangled method");
    }

    [Fact]
    public void AFunctionArgument_FoldsHasattrFalse_AndBindsTheFunctionField()
    {
        var ir = Gen(Shape +
            "d = D(pred)\n" +
            "x = d.read()\n");

        var main = ir.Functions.Single(f => f.Name == "main");
        main.Body.Any(i => i is Call c && c.FunctionName == "pred").Should().BeTrue(
            "self.f() on the Callable branch dispatches to the bound function");
        main.Body.Any(i => i is Copy { Dst: Variable dv } && dv.Name.EndsWith("_f"))
            .Should().BeFalse("self.f is a compile-time binding, not a stored scalar");
    }

    [Fact]
    public void HasattrOnAnUnpinnedParameter_StillRefuses()
    {
        var act = () => Gen(
            "def pick(io):\n" +
            "    if hasattr(io, \"value\"):\n" +
            "        return 1\n" +
            "    return 0\n\n" +
            "a = pick(5)\n");
        // `io` in a once-lowered body is a runtime register: no shape to
        // answer with, so the honest refusal stands.
        act.Should().Throw<Exception>().WithMessage("*runtime reflection*");
    }
}
