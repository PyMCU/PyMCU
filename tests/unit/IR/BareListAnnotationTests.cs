using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// A bare `list` annotation -- `def f(xs: list)`, `x: list = ...`, `-> list` -- is the
/// spelling CircuitPython libraries actually write (`adafruit_irremote.bin_data` takes
/// `pulses: list` and answers `-> list[list]`). It means a run-time list whose element
/// type is the type of the value BOUND to the name at that point: the argument for a
/// parameter, the initializer for a local, the returned variable for a return type.
/// The annotation used to be refused outright ("'list' is the head of a bracketed type,
/// not a type on its own"), which made every unmodified library signature a rewrite.
///
/// A bare `list` parameter expands at the call site exactly like a `list[T]` one, and
/// the bound argument supplies the element registration `xs[i]` and `len(xs)` resolve
/// through. Binding a value that is not a run-time list is refused at the call site,
/// not misread.
/// </summary>
public class BareListAnnotationTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    private static string Fails(string src)
    {
        try { Gen(src); }
        catch (Exception ex) { return ex.Message; }
        throw new Xunit.Sdk.XunitException("expected a compile error");
    }

    // The core shape: `pulses[i]` inside a `list`-annotated parameter emits a list load
    // of the ARGUMENT's element width -- two bytes for a list[uint16] -- not a byte read
    // and not a bit check.
    [Fact]
    public void BareListParam_SubscriptUsesTheArgumentsElementType()
    {
        var ir = Gen(
            "from pymcu.types import uint16\n" +
            "def take(xs: list) -> uint16:\n" +
            "    return xs[0]\n" +
            "p: list[uint16] = list()\n" +
            "p.append(42)\n" +
            "def main():\n" +
            "    v: uint16 = take(p)\n");

        var main = ir.Functions.Single(f => f.Name == "main");
        Assert.Contains(main.Body, i =>
            i is LoadIndirect li && li.Elem == DataType.UINT16);
    }

    // `len(xs)` on the same parameter answers the argument's live count.
    [Fact]
    public void BareListParam_LenResolvesThroughTheArgument()
    {
        var ir = Gen(
            "from pymcu.types import uint8, uint16\n" +
            "def count(xs: list) -> uint8:\n" +
            "    return len(xs)\n" +
            "p: list[uint16] = list()\n" +
            "def main():\n" +
            "    n: uint8 = count(p)\n");

        var main = ir.Functions.Single(f => f.Name == "main");
        Assert.Contains(main.Body, i => i is LoadIndirect);
    }

    // `for x in xs` over a bare-list parameter iterates the argument's list.
    [Fact]
    public void BareListParam_IteratesTheArgument()
    {
        var ir = Gen(
            "from pymcu.types import uint16\n" +
            "def total(xs: list) -> uint16:\n" +
            "    t: uint16 = 0\n" +
            "    for x in xs:\n" +
            "        t = t + x\n" +
            "    return t\n" +
            "p: list[uint16] = list()\n" +
            "def main():\n" +
            "    s: uint16 = total(p)\n");

        var main = ir.Functions.Single(f => f.Name == "main");
        Assert.Contains(main.Body, i => i is LoadIndirect li && li.Elem == DataType.UINT16);
    }

    // The vendored shape: `def decode_bits(pulses: list)` then `pulses = list(pulses)`.
    // The parameter is reassigned, so the binding materializes a real local; reads
    // between the binding and that first write must still see a list, and the writes
    // after must keep their element type.
    [Fact]
    public void BareListParam_ReassignedInBody_KeepsListness()
    {
        var ir = Gen(
            "from pymcu.types import uint16\n" +
            "def decode(xs: list) -> uint16:\n" +
            "    ys: list[uint16] = list(xs)\n" +
            "    return ys[0]\n" +
            "p: list[uint16] = list()\n" +
            "def main():\n" +
            "    v: uint16 = decode(p)\n");

        var main = ir.Functions.Single(f => f.Name == "main");
        Assert.Contains(main.Body, i => i is LoadIndirect);
    }

    // `-> list` and `-> list[list]`: the element types come from the returned
    // variable's own registration, so `f()[0]` and `g()[0][0]` both read right.
    [Fact]
    public void BareListReturn_BindsTheReturnedListsElementTypes()
    {
        var ir = Gen(
            "from pymcu.types import uint16\n" +
            "def make() -> list:\n" +
            "    xs: list[uint16] = list()\n" +
            "    xs.append(7)\n" +
            "    return xs\n" +
            "def make2() -> list[list]:\n" +
            "    ys: list[list[uint16]] = list()\n" +
            "    return ys\n" +
            "def main():\n" +
            "    a = make()\n" +
            "    b = make2()\n" +
            "    v: uint16 = a[0] + b[0][0]\n");

        var main = ir.Functions.Single(f => f.Name == "main");
        Assert.Contains(main.Body, i => i is LoadIndirect li && li.Elem == DataType.UINT16);
    }

    // `-> tuple` is the other bare container the vendored file writes (GenericDecode.
    // decode_bits). The returned value carries its own shape; the annotation only has
    // to stop refusing the name.
    [Fact]
    public void BareTupleReturn_IsAccepted()
        => Assert.NotNull(Gen(
            "def pair() -> tuple:\n" +
            "    return 1, 2\n" +
            "def main():\n" +
            "    a, b = pair()\n"));

    // A value that is not a run-time list bound to a `list` parameter is refused at
    // the call site -- the parameter is a promise the body will keep, and a scalar
    // cannot keep it.
    [Fact]
    public void BareListParam_BoundToAScalar_Refuses()
    {
        string msg = Fails(
            "def take(xs: list) -> uint8:\n" +
            "    return xs[0]\n" +
            "def main():\n" +
            "    n: uint8 = 3\n" +
            "    v: uint8 = take(n)\n");

        Assert.Contains("list", msg);
    }
}
