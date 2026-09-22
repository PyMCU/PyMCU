using FluentAssertions;
using PyMCU.Common;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// The compile-time evaluations the unmodified CircuitPython sources need, each reduced
/// to the smallest program that asks for it: sys.implementation.version[i] and
/// getattr(module, "name", default) feature-detection (neopixel.py), a string carried
/// through a None-defaulted parameter and a ternary (pixelbuf's pixel_order), the
/// tuple-return unpack of parse_byteorder, isinstance(x, slice) in __setitem__,
/// range(*slice.indices(...)), a raise inside an except handler, and the type-alias
/// assignments the try/except typing guard leaves behind.
/// </summary>
public class CircuitPythonCompatTests
{
    private const string Regs =
        "from pymcu.chips.atmega328p import GPIOR0, GPIOR1\n" +
        "from pymcu.types import uint8, int32, inline\n\n";

    // The compat layer's sys.py carries only `implementation.name`; `version` is
    // answered by the compiler's introspection table, which keys off the project's
    // stdlib layer -- the DeviceConfig below.
    private const string SysShim =
        "class _Implementation:\n" +
        "    def __init__(self):\n" +
        "        self.name = \"circuitpython\"\n" +
        "implementation = _Implementation()\n";

    private const string BoardShim =
        "NEOPIXEL = \"PD1\"\n" +
        "D6 = \"PD6\"\n";

    private static ProgramNode Parse(string src) =>
        new Parser(new Lexer(src).Tokenize()).ParseProgram();

    private static ProgramIR Gen(string src, Dictionary<string, ProgramNode>? modules = null,
        string? stdlib = null)
    {
        // ConditionalCompilator is the pipeline stage that folds optional-import guards and
        // records `X = Union[...]` as a type alias; running it keeps the test on the path
        // the driver takes rather than asking IRGenerator to see statements it never does.
        var config = new DeviceConfig { Arch = "avr", Stdlib = stdlib ?? "" };
        var mainAst = Parse(src);
        var cc = new ConditionalCompilator(config) { ModuleName = "__main__" };
        cc.Process(mainAst);
        var mods = modules ?? new Dictionary<string, ProgramNode>();
        foreach (var (modName, modAst) in mods)
            new ConditionalCompilator(config) { ModuleName = modName }.Process(modAst);
        return new IRGenerator().Generate(mainAst, mods, config);
    }

    private static List<Instruction> Main(ProgramIR ir) =>
        ir.Functions.Single(f => f.Name == "main").Body;

    private static bool WritesGpio(List<Instruction> body, string reg, int value) =>
        body.Any(i => i is Copy { Src: Constant { Value: var v }, Dst: Variable { Name: var n } }
                      && v == value && n.EndsWith(reg, StringComparison.Ordinal));

    // ── sys.implementation.version[i] ─────────────────────────────────────────────

    [Fact]
    public void VersionIndexFoldsInsideAFunctionBody()
    {
        // neopixel.py: `sys.implementation.version[0] >= 7` inside __init__. The
        // frontend's compile-time `if` fold does not reach into a function body, so the
        // subscript itself answers with the layer's version.
        var ir = Gen(
            Regs + "import sys\n\n" +
            "def main():\n" +
            "    if sys.implementation.version[0] >= 7:\n" +
            "        GPIOR0.value = 9\n" +
            "    else:\n" +
            "        GPIOR0.value = 3\n",
            new Dictionary<string, ProgramNode> { ["sys"] = Parse(SysShim) },
            stdlib: "circuitpython");

        WritesGpio(Main(ir), "GPIOR0", 9).Should().BeTrue(
            "the circuitpython layer reports version (8, x, y): version[0] >= 7 is true");
        WritesGpio(Main(ir), "GPIOR0", 3).Should().BeFalse();
    }

    [Fact]
    public void VersionIndexOnTheBareStdlibIsRefused()
    {
        // No compat layer selected: there is no CircuitPython version to report, and
        // answering anyway would let the guard pass on a build that cannot run the code.
        var ex = Assert.ThrowsAny<CompilerError>(() => Gen(
            Regs + "import sys\n\n" +
            "def main():\n" +
            "    x: int32 = sys.implementation.version[0]\n",
            new Dictionary<string, ProgramNode> { ["sys"] = Parse(SysShim) }));
        Assert.Contains("sys.implementation.version", ex.Message);
    }

    // ── getattr(module, "name", default) ─────────────────────────────────────────

    [Fact]
    public void GetattrOnAnExportedMemberReadsTheMember()
    {
        var ir = Gen(
            Regs + "import board\n\n" +
            "def main():\n" +
            "    p = getattr(board, \"NEOPIXEL\", None)\n" +
            "    if p is not None:\n" +
            "        GPIOR0.value = 9\n",
            new Dictionary<string, ProgramNode> { ["board"] = Parse(BoardShim) });
        WritesGpio(Main(ir), "GPIOR0", 9).Should().BeTrue("board exports NEOPIXEL");
    }

    [Fact]
    public void GetattrOnAMissingMemberReadsTheDefault()
    {
        // The neopixel.py shape: `power = getattr(board, "NEOPIXEL_POWER", None)` on a
        // board with no such pin. The None must reach `if power:` / `power is None` as a
        // compile-time None, or the dead DigitalInOut(power) call still lowers.
        var ir = Gen(
            Regs + "import board\n\n" +
            "def main():\n" +
            "    power = getattr(board, \"NEOPIXEL_POWER\", None)\n" +
            "    if power:\n" +
            "        GPIOR0.value = 9\n" +
            "    if power is None:\n" +
            "        GPIOR1.value = 7\n",
            new Dictionary<string, ProgramNode> { ["board"] = Parse(BoardShim) });
        WritesGpio(Main(ir), "GPIOR0", 9).Should().BeFalse("power is None: the body is dead");
        WritesGpio(Main(ir), "GPIOR1", 7).Should().BeTrue();
    }

    // ── a None-bound name rebound to a string ─────────────────────────────────────

    [Fact]
    public void AParamReboundFromNoneToAStringKeepsTheString()
    {
        // pixelbuf's `pixel_order: str = None` then `pixel_order = GRB if ...`: the
        // None-ness of the default must not survive the rebound name, or len() on it
        // answers like it is still None.
        var ir = Gen(
            Regs +
            "GRB = \"GRB\"\n" +
            "@inline\n" +
            "def n(bpp: uint8, pixel_order = None) -> int32:\n" +
            "    if not pixel_order:\n" +
            "        pixel_order = GRB if bpp == 3 else \"GRBW\"\n" +
            "    return len(pixel_order)\n\n" +
            "def main():\n" +
            "    GPIOR0.value = n(3)\n");
        // The call's value arrives as its result temporary; the compile-time answer is
        // visible on the store INTO it, which must be the folded 3 of len("GRB").
        Main(ir).Any(i => i is Copy { Src: Constant { Value: 3 }, Dst: Temporary })
            .Should().BeTrue("pixel_order is \"GRB\" at the call, so len is 3 -- not a stale None");
    }

    // ── the parse_byteorder tuple return ──────────────────────────────────────────

    [Fact]
    public void ATupleReturnKeepsItsElementSequences()
    {
        // `bpp, byteorder_tuple, has_white, dotstar_mode = self.parse_byteorder(...)`.
        // The flag must stay a compile-time constant through the unpack, or
        // `if dotstar_mode:` lowers its dead body; the tuple element must keep its
        // sequence identity, or `byteorder_tuple[0] + 1` inside that body refuses.
        var ir = Gen(
            Regs +
            "class P:\n" +
            "    def __init__(self) -> None:\n" +
            "        self._t: uint8 = 0\n" +
            "    @inline\n" +
            "    def parse(self, s):\n" +
            "        r = s.index(\"R\")\n" +
            "        t = (r, 1, 2)\n" +
            "        return 3, t, False, False\n" +
            "    @inline\n" +
            "    def init2(self, s) -> None:\n" +
            "        bpp, tup, has_white, dotstar_mode = self.parse(s)\n" +
            "        if dotstar_mode:\n" +
            "            self._t = tup[0] + 1\n" +
            "        GPIOR0.value = bpp\n\n" +
            "p = P()\n" +
            "def main():\n" +
            "    p.init2(\"GRB\")\n");
        WritesGpio(Main(ir), "GPIOR0", 3).Should().BeTrue(
            "bpp crossed the unpack as the constant 3");
    }

    // ── isinstance(x, slice) ──────────────────────────────────────────────────────

    [Fact]
    public void IsInstanceSliceNeverMatchesAValue()
    {
        // __setitem__'s `isinstance(index, slice)`: a subscript's slice reaches the dunder
        // as syntax, not as a bound slice object, so nothing that flows in is one.
        var ir = Gen(
            Regs +
            "def main():\n" +
            "    i: uint8 = 0\n" +
            "    if isinstance(i, slice):\n" +
            "        GPIOR0.value = 9\n" +
            "    else:\n" +
            "        GPIOR0.value = 3\n");
        WritesGpio(Main(ir), "GPIOR0", 3).Should().BeTrue("an int is never a slice");
    }

    // ── range(*tuple) in a for header ─────────────────────────────────────────────

    [Fact]
    public void RangeSplicedWithAConstantTupleUnrolls()
    {
        // `range(*index.indices(n))` in __setitem__'s slice branch: the starred tuple is
        // spliced into the call's argument list before the range bound check runs.
        var ir = Gen(
            Regs +
            "def main():\n" +
            "    t = (0, 3)\n" +
            "    for i in range(*t):\n" +
            "        GPIOR0.value = i\n");
        WritesGpio(Main(ir), "GPIOR0", 0).Should().BeTrue();
        WritesGpio(Main(ir), "GPIOR0", 2).Should().BeTrue();
        WritesGpio(Main(ir), "GPIOR0", 3).Should().BeFalse("range(0, 3) stops before 3");
    }

    [Fact]
    public void RangeSplicedWithATupleCallRunsAsACounterLoop()
    {
        // `for i in range(*t())` used to refuse: the dedicated unroller needed
        // compile-time bounds, and the splice delivers the call's tuple result
        // slots as pre-evaluated values, not literals. Routed through the
        // canonical range path it is the same loop `for i in range(a, b)` over
        // variables is -- a runtime counter bounded by the evaluated elements.
        var ir = Gen(
            Regs +
            "def t():\n" +
            "    return (0, 3)\n" +
            "def main():\n" +
            "    for i in range(*t()):\n" +
            "        GPIOR0.value = i\n");
        var body = Main(ir);
        // The call's result slots carry the bounds (0, 3), written once.
        body.Any(i => i is Copy { Src: Constant { Value: 0 }, Dst: Variable { Name: var n } }
                      && n.Contains("iret_", StringComparison.Ordinal))
            .Should().BeTrue("the spliced start is evaluated once into a slot");
        body.Any(i => i is Copy { Src: Constant { Value: 3 }, Dst: Variable { Name: var n } }
                      && n.Contains("iret_", StringComparison.Ordinal))
            .Should().BeTrue("the spliced stop is evaluated once into a slot");
        // One counter loop over those slots, not three copies of the body.
        body.OfType<JumpIfGreaterOrEqual>().Should().HaveCount(1,
            "a runtime-bounded range lowers to a single counter loop");
        body.Count(i => i is Copy { Dst: Variable { Name: var n } }
                        && n.EndsWith("GPIOR0", StringComparison.Ordinal))
            .Should().Be(1, "the loop body is emitted once, not unrolled");
    }

    // ── raise inside an exception handler ─────────────────────────────────────────

    [Fact]
    public void ARaiseInsideAHandlerIsNotAnUnconditionalAbort()
    {
        // `except ValueError: raise` is reached only if the try raised. Lowering the
        // handler must not abort the whole build as though the raise always ran --
        // pixelbuf wraps its index() calls in exactly this.
        var ir = Gen(
            Regs +
            "def main():\n" +
            "    try:\n" +
            "        GPIOR0.value = 5\n" +
            "    except ValueError:\n" +
            "        raise\n" +
            "    GPIOR1.value = 7\n");
        WritesGpio(Main(ir), "GPIOR0", 5).Should().BeTrue();
        WritesGpio(Main(ir), "GPIOR1", 7).Should().BeTrue(
            "the statement after the try still lowers");
    }

    // ── open() names the file ─────────────────────────────────────────────────────

    [Fact]
    public void OpenNamesTheFileAndTheMissingEmbed()
    {
        // RFC 0008: open() resolves at compile time against the embedded-file
        // table. A name the build did not embed is a diagnostic naming the file
        // and the two ways to embed it -- `files = [...]` or auto-embedding.
        var ex = Assert.ThrowsAny<CompilerError>(() => Gen(
            Regs +
            "def main():\n" +
            "    name = \"font5x8.bin\"\n" +
            "    f = open(name, \"rb\")\n"));
        Assert.Contains("font5x8.bin", ex.Message);
        Assert.Contains("embedded", ex.Message);
    }

    // ── a Union alias assigned at module level ────────────────────────────────────

    [Fact]
    public void AUnionAliasResolvesInAnnotations()
    {
        // `ColorUnion = Union[int, Tuple[int, int, int], ...]` at module scope -- the
        // spelling adafruit_pixelbuf uses inside its try/except typing guard. A
        // parameter annotated with the alias resolves per call site, which asks for
        // a callee that expands there -- @inline, or a method that never outlines.
        var ir = Gen(
            Regs +
            "ColorUnion = Union[int, uint8]\n" +
            "@inline\n" +
            "def take(c: ColorUnion) -> uint8:\n" +
            "    return c\n\n" +
            "def main():\n" +
            "    GPIOR0.value = take(5)\n");
        // `take` returns its parameter: the call's result temp is stored from the
        // bound constant, which is where the alias resolution is visible.
        Main(ir).Any(i => i is Copy { Src: Constant { Value: 5 }, Dst: Temporary })
            .Should().BeTrue("an int argument satisfies Union[int, uint8] under its alias");
    }
}
