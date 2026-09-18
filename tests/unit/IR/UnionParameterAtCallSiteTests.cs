using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// `Union[A, B]` on a parameter of an @inline-expanded function or method (every constructor
/// included -- a ZCA instance always builds at its call site): read as "the type of the
/// argument at THIS site, which must be one of the members", exactly how an @inline overload
/// already dispatches on an argument's type. A `Union[A, B]` on a REAL subroutine's parameter
/// keeps its refusal -- one ABI, no call site to resolve it at.
///
/// Found via three Adafruit libraries: adafruit_character_lcd's `Union[pwmio.PWMOut,
/// digitalio.DigitalInOut]`, adafruit_debouncer's `Union[ROValueIO, Callable[[], bool]]`, and
/// adafruit_ht16k33's `Union[int, List[int], Tuple[int, ...]]`.
/// </summary>
public class UnionParameterAtCallSiteTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    private static string Refusal(string src) =>
        Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(src)).Message;

    private const string Classes =
        "from pymcu.types import used\n\n" +
        "class A:\n    def __init__(self, x: int) -> None:\n        self.x: int = x\n\n" +
        "class B:\n    def __init__(self, y: int) -> None:\n        self.y: int = y\n\n" +
        "class Holder:\n    def __init__(self, thing: Union[A, B]) -> None:\n        self.thing = thing\n\n";

    [Fact]
    public void AConstructorArgumentMatchingTheFirstMemberFoldsThroughTheField()
    {
        // Red before the fix: "a union type annotation is not supported".
        var ir = Gen(Classes + "@used\ndef main() -> int:\n    h = Holder(A(5))\n    return h.thing.x\n");
        var main = ir.Functions.Single(f => f.Name == "main");
        Assert.Contains(main.Body, i => i is Return r && r.Value is Constant { Value: 5 });
    }

    [Fact]
    public void AConstructorArgumentMatchingTheSecondMemberFoldsThroughTheField()
    {
        var ir = Gen(Classes + "@used\ndef main() -> int:\n    h = Holder(B(7))\n    return h.thing.y\n");
        var main = ir.Functions.Single(f => f.Name == "main");
        Assert.Contains(main.Body, i => i is Return r && r.Value is Constant { Value: 7 });
    }

    [Fact]
    public void AnArgumentMatchingNoMemberIsRefusedNamingTheMembers()
    {
        string msg = Refusal(Classes + "@used\ndef main() -> int:\n    h = Holder(42)\n    return 0\n");
        Assert.Contains("Union[A, B]", msg);
        Assert.Contains("matches none", msg);
    }

    [Fact]
    public void AUnionOfIntAndAFixedArrayAcceptsBoth()
    {
        // adafruit_ht16k33's exact shape: Union[int, List[int], Tuple[int, ...]] = 0x70.
        var ir = Gen(
            "from pymcu.types import used\n\n" +
            "class Matrix:\n" +
            "    def __init__(self, address: Union[int, List[int], Tuple[int, ...]] = 0x70) -> None:\n" +
            "        self.address = address\n\n" +
            "@used\n" +
            "def main() -> int:\n" +
            "    m1 = Matrix(0x70)\n" +
            "    m2 = Matrix([1, 2, 3])\n" +
            "    return m1.address + m2.address[0]\n");
        var main = ir.Functions.Single(f => f.Name == "main");
        Assert.Contains(main.Body, i => i is Return r && r.Value is Constant { Value: 113 });
    }

    [Fact]
    public void AProtocolMemberAcceptsAStructurallyMatchingClass()
    {
        // adafruit_debouncer / PyMCU#465: ROValueIO is a Protocol with a `.value` property.
        // DigitalInOut is not named ROValueIO, but it has that property, and CPython accepts it.
        var ir = Gen(
            "from pymcu.types import used, uint8\n\n" +
            "class ROValueIO(Protocol):\n" +
            "    @property\n" +
            "    def value(self) -> uint8: ...\n\n" +
            "class Pin:\n" +
            "    def __init__(self, v: uint8) -> None:\n" +
            "        self.value: uint8 = v\n\n" +
            "class Debouncer:\n" +
            "    def __init__(self, io_or_predicate: Union[ROValueIO, Callable[[], bool]]) -> None:\n" +
            "        self._io = io_or_predicate\n\n" +
            "@used\n" +
            "def main() -> uint8:\n" +
            "    d = Debouncer(Pin(5))\n" +
            "    return d._io.value\n");
        // The discriminator is that it compiles. The field read may not fold through
        // the constructor the way a same-class field does (#446); Constant 5 is still
        // the Pin constructor argument.
        Assert.Contains(ir.Functions.SelectMany(f => f.Body).OfType<Copy>(),
            c => c.Src is Constant { Value: 5 });
    }

    [Fact]
    public void AProtocolMemberStillAcceptsAPlainFunctionReference()
    {
        var ir = Gen(
            "from pymcu.types import used, uint8\n\n" +
            "class ROValueIO(Protocol):\n" +
            "    @property\n" +
            "    def value(self) -> uint8: ...\n\n" +
            "def my_predicate() -> uint8:\n    return 1\n\n" +
            "class Debouncer:\n" +
            "    def __init__(self, io_or_predicate: Union[ROValueIO, Callable[[], bool]]) -> None:\n" +
            "        self.thing = io_or_predicate\n\n" +
            "@used\n" +
            "def main() -> uint8:\n" +
            "    d = Debouncer(my_predicate)\n" +
            "    return 1\n");
        Assert.NotEmpty(ir.Functions.Single(f => f.Name == "main").Body);
    }

    [Fact]
    public void AClassMissingTheProtocolMembersIsRefused()
    {
        string msg = Refusal(
            "from pymcu.types import used, uint8\n\n" +
            "class ROValueIO(Protocol):\n" +
            "    @property\n" +
            "    def value(self) -> uint8: ...\n\n" +
            "class Other:\n" +
            "    def __init__(self, x: uint8) -> None:\n" +
            "        self.x: uint8 = x\n\n" +
            "class Debouncer:\n" +
            "    def __init__(self, io_or_predicate: Union[ROValueIO, Callable[[], bool]]) -> None:\n" +
            "        self._io = io_or_predicate\n\n" +
            "@used\n" +
            "def main() -> uint8:\n" +
            "    d = Debouncer(Other(1))\n" +
            "    return 0\n");
        Assert.Contains("ROValueIO", msg);
        Assert.Contains("matches none", msg);
    }

    [Fact]
    public void AUnionOfAClassAndACallableAcceptsBoth()
    {
        // adafruit_debouncer's exact shape: Union[ROValueIO, Callable[[], bool]].
        var ir = Gen(
            "from pymcu.types import used, uint8\n\n" +
            "class ROValueIO:\n    def __init__(self, v: uint8) -> None:\n        self.value: uint8 = v\n\n" +
            "def my_predicate() -> uint8:\n    return 1\n\n" +
            "class Debouncer:\n" +
            "    def __init__(self, io_or_predicate: Union[ROValueIO, Callable[[], bool]]) -> None:\n" +
            "        self.thing = io_or_predicate\n\n" +
            "@used\n" +
            "def main() -> uint8:\n" +
            "    d1 = Debouncer(ROValueIO(5))\n" +
            "    d2 = Debouncer(my_predicate)\n" +
            "    return d1.thing.value\n");
        var main = ir.Functions.Single(f => f.Name == "main");
        // Not necessarily folded to a constant (the field is a real read), but it must compile
        // both call sites without refusing either shape.
        Assert.NotEmpty(main.Body);
    }

    [Fact]
    public void AUnionParameterOnARegularFunctionKeepsItsRefusal()
        => Assert.Contains("union type annotation is not supported",
            Refusal("def take(x: Union[int, float]) -> int:\n    return 1\n\ndef main() -> None:\n    pass\n"));

    [Fact]
    public void OptionalIsUnaffected()
    {
        var ir = Gen(
            "from pymcu.types import used\n\n" +
            "def take(x: Optional[int] = None) -> int:\n" +
            "    if x is None:\n        return 0\n    return x\n\n" +
            "@used\ndef main() -> int:\n    return take(5) + take()\n");
        Assert.NotEmpty(ir.Functions);
    }
}
