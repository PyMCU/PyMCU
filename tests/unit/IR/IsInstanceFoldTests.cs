using FluentAssertions;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU#424. <c>isinstance(x, T)</c> on a ZCA instance is not a decision, it is a fold: every
/// instance has a class fixed when the program is compiled, so the answer -- true when x's
/// class is T or a subclass of T, false otherwise -- is already known. Reduced from
/// adafruit_mcp3xxx's own <c>AnalogIn.__init__</c>:
/// <c>if not isinstance(mcp, MCP3xxx): raise ValueError(...)</c>.
///
/// #423 extends the same fold to the builtins <c>tuple</c>/<c>list</c>/<c>int</c> from a
/// receiver whose shape is already known (adafruit_ht16k33's address argument).
/// </summary>
[Trait("Issue", "423")]
[Trait("Issue", "424")]
public class IsInstanceFoldTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });

    private static List<Instruction> Main(string src) => Gen(src).Functions.Single(f => f.Name == "main").Body;

    private const string Preamble =
        "from pymcu.chips.atmega328p import GPIOR0, GPIOR1\n" +
        "from pymcu.types import uint8\n\n\n";

    private const string Classes =
        "class Base:\n" +
        "    def __init__(self, v: uint8) -> None:\n" +
        "        self._v: uint8 = v\n\n" +
        "class Sub(Base):\n" +
        "    pass\n\n" +
        "class Other:\n" +
        "    def __init__(self) -> None:\n" +
        "        self._x: uint8 = 0\n\n";

    private static bool CopiesToGpior(List<Instruction> body, int value) =>
        body.Any(i => i is Copy { Src: Constant { Value: var v }, Dst: Variable { Name: var n } }
                      && v == value && n.EndsWith("GPIOR1", StringComparison.Ordinal));

    private static bool HasRuntimeJump(List<Instruction> body) =>
        body.Any(i => i is JumpIfZero or JumpIfNotZero);

    [Fact]
    public void TrueForTheDeclaredClassItself()
    {
        var body = Main(Preamble + Classes +
            "b = Base(5)\n" +
            "if isinstance(b, Base):\n" +
            "    GPIOR1.value = 9\n" +
            "else:\n" +
            "    GPIOR1.value = 3\n");

        CopiesToGpior(body, 9).Should().BeTrue(
            because: "Base(5) IS a Base, so the true arm is the whole program");
        CopiesToGpior(body, 3).Should().BeFalse(
            because: "the false arm is dead once the fold answers True");
        HasRuntimeJump(body).Should().BeFalse(
            because: "a compile-time fold must not leave a runtime type test");
    }

    [Fact]
    public void TrueForASubclassOfTheDeclaredClass()
    {
        var body = Main(Preamble + Classes +
            "s = Sub(5)\n" +
            "if isinstance(s, Base):\n" +
            "    GPIOR1.value = 9\n" +
            "else:\n" +
            "    GPIOR1.value = 3\n");

        CopiesToGpior(body, 9).Should().BeTrue(
            because: "MCP3008-as-MCP3xxx is the reported shape: a subclass still matches the base");
        CopiesToGpior(body, 3).Should().BeFalse(
            because: "the false arm is dead once the fold answers True");
        HasRuntimeJump(body).Should().BeFalse(
            because: "a compile-time fold must not leave a runtime type test");
    }

    [Fact]
    public void FalseForAnUnrelatedClass()
    {
        var body = Main(Preamble + Classes +
            "b = Base(5)\n" +
            "if isinstance(b, Other):\n" +
            "    GPIOR1.value = 9\n" +
            "else:\n" +
            "    GPIOR1.value = 3\n");

        CopiesToGpior(body, 3).Should().BeTrue(
            because: "Base is not Other, so the false arm is the whole program");
        CopiesToGpior(body, 9).Should().BeFalse(
            because: "the true arm is dead once the fold answers False");
        HasRuntimeJump(body).Should().BeFalse(
            because: "a compile-time fold must not leave a runtime type test");
    }

    [Fact]
    public void TrueWhenAnyMemberOfATupleOfTypesMatches()
    {
        var body = Main(Preamble + Classes +
            "s = Sub(5)\n" +
            "if isinstance(s, (Other, Base)):\n" +
            "    GPIOR1.value = 9\n" +
            "else:\n" +
            "    GPIOR1.value = 3\n");

        CopiesToGpior(body, 9).Should().BeTrue(
            because: "isinstance(x, (A, B)) is true when either candidate matches");
        HasRuntimeJump(body).Should().BeFalse(
            because: "a compile-time fold must not leave a runtime type test");
    }

    [Fact]
    public void FalseWhenTheBoundValueIsAStringNotTheDeclaredInstance()
    {
        // `Wrap("nope")`: the declared parameter type is Base, the value bound at
        // this call site is a string -- isinstance answers on the VALUE, so the
        // guard's raise is the taken branch and the rest of __init__ never lowers.
        // While the annotation outranked the binding, the tail lowered anyway and
        // `m._v` on a string failed "Unknown member access" -- adafruit_mcp3xxx's
        // `AnalogIn("not an mcp", P0)` reported exactly that.
        var body = Main(Preamble + Classes +
            "class Wrap:\n" +
            "    def __init__(self, m: Base) -> None:\n" +
            "        if not isinstance(m, Base):\n" +
            "            raise ValueError(\"not a base\")\n" +
            "        self._v = m._v\n\n" +
            "try:\n" +
            "    w = Wrap(\"nope\")\n" +
            "except ValueError:\n" +
            "    GPIOR1.value = 9\n");

        CopiesToGpior(body, 9).Should().BeTrue(
            because: "the except arm is reached through the raise the fold says always runs");
        body.Where(i => i is Copy { Dst: Variable { Name: var n } } && n.EndsWith("__v"))
            .Should().BeEmpty(
                because: "self._v = m._v sits after an unconditional raise and must not lower");
    }

    [Fact]
    public void TheReportedShapeBuildsWithNoUnraisedException()
    {
        var body = Main(Preamble + Classes +
            "class AnalogIn:\n" +
            "    def __init__(self, mcp: Base, pin: uint8) -> None:\n" +
            "        if not isinstance(mcp, Base):\n" +
            "            raise ValueError(\"bad\")\n" +
            "        self._mcp: Base = mcp\n" +
            "        self._pin: uint8 = pin\n\n" +
            "s = Sub(5)\n" +
            "a = AnalogIn(s, 0)\n" +
            "GPIOR1.value = 1\n");

        CopiesToGpior(body, 1).Should().BeTrue(
            because: "the constructor used to refuse isinstance() outright; now it has to build");
        HasRuntimeJump(body).Should().BeFalse(
            because: "the never-taken raise must not leave a Jump behind to reach it");
    }

    [Fact]
    public void FalseWhenTheReceiverIsAScalarAndTheCandidatesAreTupleAndList()
    {
        var body = Main(Preamble +
            "n: uint8 = 0x70\n" +
            "if isinstance(n, (tuple, list)):\n" +
            "    GPIOR1.value = 9\n" +
            "else:\n" +
            "    GPIOR1.value = 3\n");

        CopiesToGpior(body, 3).Should().BeTrue(
            because: "ht16k33's default address 0x70 is an int, so the scalar arm is taken");
        CopiesToGpior(body, 9).Should().BeFalse(
            because: "a uint8 is neither tuple nor list");
        HasRuntimeJump(body).Should().BeFalse(
            because: "the shape is known at the call site, so there is no runtime test");
    }

    [Fact]
    public void TrueWhenTheReceiverIsAListLiteralAndTheCandidatesAreTupleAndList()
    {
        var body = Main(Preamble +
            "n = [1, 2, 3]\n" +
            "if isinstance(n, (tuple, list)):\n" +
            "    GPIOR1.value = 9\n" +
            "else:\n" +
            "    GPIOR1.value = 3\n");

        CopiesToGpior(body, 9).Should().BeTrue(
            because: "a list literal matches isinstance(..., (tuple, list))");
        HasRuntimeJump(body).Should().BeFalse(
            because: "the shape is known at the call site, so there is no runtime test");
    }

    [Fact]
    public void AConstructorUnionFoldsIsinstancePerCallSite()
    {
        var body = Main(Preamble +
            "class Matrix:\n" +
            "    def __init__(self, address: Union[uint8, List[uint8], Tuple[uint8, ...]] = 0x70) -> None:\n" +
            "        if isinstance(address, (tuple, list)):\n" +
            "            self.address = address[0]\n" +
            "        else:\n" +
            "            self.address = address\n\n" +
            "m1 = Matrix()\n" +
            "m2 = Matrix([1, 2, 3])\n" +
            "GPIOR1.value = m1.address + m2.address\n");

        CopiesToGpior(body, 113).Should().BeTrue(
            because: "0x70 + the list's first element 1 is 113, each arm taken at its own call site");
        HasRuntimeJump(body).Should().BeFalse(
            because: "Union is resolved per call site, so both isinstance tests fold away");
    }

    // ── receivers spelled through bindings the flags used to miss (neopixel) ────────────
    //
    // neopixel's `elif isinstance(pixel_order, tuple)` asks about a constructor parameter
    // bound through a keyword argument to a module-level string constant. The parameter
    // name holds only an alias to the caller's name, and the module string's TEXT is filed
    // under `main.ORDER` while the bare name holds the interned-id slot -- neither spelling
    // is the one a bare probe of `pixel_order` asks about.

    [Fact]
    public void TupleCandidateFoldsFalseOnAModuleStringConstant()
    {
        var body = Main(Preamble +
            "ORDER = \"GRB\"\n" +
            "if isinstance(ORDER, tuple):\n" +
            "    GPIOR1.value = 9\n" +
            "else:\n" +
            "    GPIOR1.value = 3\n");

        CopiesToGpior(body, 3).Should().BeTrue(
            because: "a module-level string is not a tuple, so the false arm is the program");
        CopiesToGpior(body, 9).Should().BeFalse(
            because: "the true arm is dead once the fold answers False");
        HasRuntimeJump(body).Should().BeFalse(
            because: "the text is a compile-time fact, so no runtime test may remain");
    }

    [Fact]
    public void StrCandidateFoldsTrueOnAModuleStringConstant()
    {
        var body = Main(Preamble +
            "ORDER = \"GRB\"\n" +
            "if isinstance(ORDER, str):\n" +
            "    GPIOR1.value = 9\n" +
            "else:\n" +
            "    GPIOR1.value = 3\n");

        CopiesToGpior(body, 9).Should().BeTrue(
            because: "ORDER is a string, so the true arm is the program");
        HasRuntimeJump(body).Should().BeFalse(
            because: "the text is a compile-time fact, so no runtime test may remain");
    }

    [Fact]
    public void TupleCandidateFoldsFalseOnAParamAliasedThroughAKeywordArgument()
    {
        // The reported shape: `NeoPixel(pin, n, pixel_order=ORDER)`. `not pixel_order`
        // folds False (a non-empty string), so the rebound arm is dead and the alias
        // survives for the elif's isinstance to read.
        var body = Main(Preamble +
            "ORDER = \"GRB\"\n\n" +
            "class Px:\n" +
            "    def __init__(self, pixel_order = None) -> None:\n" +
            "        if not pixel_order:\n" +
            "            pixel_order = \"RGB\"\n" +
            "        elif isinstance(pixel_order, tuple):\n" +
            "            pixel_order = \"XXX\"\n" +
            "        self._o = pixel_order\n\n" +
            "p = Px(pixel_order=ORDER)\n" +
            "GPIOR1.value = 1\n");

        body.Any(i => i is Copy { Src: Constant { Text: "GRB" } }).Should().BeTrue(
            because: "pixel_order stays \"GRB\": the dead rebind arm never lowered and the " +
                     "tuple arm folded False, so the field store carries the original text");
        body.Any(i => i is Copy { Src: Constant { Text: "RGB" or "XXX" } }).Should().BeFalse(
            because: "neither dead arm may write pixel_order");
    }

    [Fact]
    public void TupleCandidateFoldsFalseOnANoneBoundParam()
    {
        // `NeoPixel(pin, n)` -- pixel_order defaults to None, and None is no candidate.
        var body = Main(Preamble +
            "class Px:\n" +
            "    def __init__(self, pixel_order = None) -> None:\n" +
            "        if isinstance(pixel_order, tuple):\n" +
            "            self._o = 9\n" +
            "        else:\n" +
            "            self._o = 3\n\n" +
            "p = Px()\n" +
            "GPIOR1.value = p._o\n");

        CopiesToGpior(body, 3).Should().BeTrue(
            because: "None is not a tuple, so the else arm assigns");
        CopiesToGpior(body, 9).Should().BeFalse(
            because: "the true arm is dead once the fold answers False");
    }

    [Fact]
    public void SliceCandidateFoldsFalseOnAnIntParamBoundToALoopVariable()
    {
        // pixelbuf's `if isinstance(index, slice)`: the parameter binds through an alias
        // to the caller's unrolled loop variable -- an int, and PyMCU has no slice VALUE
        // at all, so the answer is False.
        var body = Main(Preamble +
            "class Px:\n" +
            "    def __init__(self) -> None:\n" +
            "        self._v: uint8 = 0\n" +
            "    def __setitem__(self, index: uint8, val: uint8) -> None:\n" +
            "        if isinstance(index, slice):\n" +
            "            self._v = 9\n" +
            "        else:\n" +
            "            self._v = val\n\n" +
            "p = Px()\n" +
            "for i in range(2):\n" +
            "    p[i] = 5\n");

        body.Any(i => i is Copy { Src: Constant { Value: 5 } }).Should().BeTrue(
            because: "the else arm assigns val -- index is an int, never a slice");
        body.Any(i => i is Copy { Src: Constant { Value: 9 }, Dst: Variable { Name: var n } }
                      && n.Contains("_v")).Should().BeFalse(
            because: "the slice arm is dead once the fold answers False");
    }
}
