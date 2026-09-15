using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU#424. `isinstance(x, T)` on a ZCA instance is not a decision, it is a fold: every
/// instance has a class fixed when the program is compiled, so the answer -- true when x's
/// class is T or a subclass of T, false otherwise -- is already known. Reduced from
/// adafruit_mcp3xxx's own `AnalogIn.__init__`:
/// `if not isinstance(mcp, MCP3xxx): raise ValueError(...)`.
///
/// Exception objects already get this by code (#369), through the exception dispatcher's own
/// type-code comparison -- unrelated to this and unaffected by it.
/// </summary>
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

    [Fact]
    public void TrueForTheDeclaredClassItself()
    {
        // The reported shape: it used to raise UserError("isinstance() is a Python builtin
        // that PyMCU does not provide ..."). Base(5) IS a Base, and the condition folds
        // straight to True with no Jump left to test it at runtime.
        var body = Main(Preamble + Classes +
            "b = Base(5)\n" +
            "if isinstance(b, Base):\n" +
            "    GPIOR1.value = 9\n" +
            "else:\n" +
            "    GPIOR1.value = 3\n");

        Assert.Contains(body, i => i is Copy { Src: Constant { Value: 9 }, Dst: Variable { Name: var n } } && n.EndsWith("GPIOR1", StringComparison.Ordinal));
        Assert.DoesNotContain(body, i => i is Copy { Src: Constant { Value: 3 }, Dst: Variable { Name: var n2 } } && n2.EndsWith("GPIOR1", StringComparison.Ordinal));
        Assert.DoesNotContain(body, i => i is JumpIfZero or JumpIfNotZero);
    }

    [Fact]
    public void TrueForASubclassOfTheDeclaredClass()
    {
        // adafruit_mcp3xxx's exact shape: the parameter is declared as the BASE class, the
        // actual instance passed is a SUBCLASS (MCP3008 as MCP3xxx). Sub(5) IS a Base too.
        var body = Main(Preamble + Classes +
            "s = Sub(5)\n" +
            "if isinstance(s, Base):\n" +
            "    GPIOR1.value = 9\n" +
            "else:\n" +
            "    GPIOR1.value = 3\n");

        Assert.Contains(body, i => i is Copy { Src: Constant { Value: 9 }, Dst: Variable { Name: var n } } && n.EndsWith("GPIOR1", StringComparison.Ordinal));
        Assert.DoesNotContain(body, i => i is Copy { Src: Constant { Value: 3 }, Dst: Variable { Name: var n2 } } && n2.EndsWith("GPIOR1", StringComparison.Ordinal));
        Assert.DoesNotContain(body, i => i is JumpIfZero or JumpIfNotZero);
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

        Assert.Contains(body, i => i is Copy { Src: Constant { Value: 3 }, Dst: Variable { Name: var n } } && n.EndsWith("GPIOR1", StringComparison.Ordinal));
        Assert.DoesNotContain(body, i => i is Copy { Src: Constant { Value: 9 }, Dst: Variable { Name: var n2 } } && n2.EndsWith("GPIOR1", StringComparison.Ordinal));
        Assert.DoesNotContain(body, i => i is JumpIfZero or JumpIfNotZero);
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

        Assert.Contains(body, i => i is Copy { Src: Constant { Value: 9 }, Dst: Variable { Name: var n } } && n.EndsWith("GPIOR1", StringComparison.Ordinal));
        Assert.DoesNotContain(body, i => i is JumpIfZero or JumpIfNotZero);
    }

    [Fact]
    public void TheReportedShapeBuildsWithNoUnraisedException()
    {
        // The issue's own shape verbatim: a constructor guarded with
        // `if not isinstance(mcp, Base): raise ValueError(...)`, called with a SUBCLASS
        // instance. It used to refuse isinstance() outright; now it has to build at all,
        // and the never-taken raise leaves no Jump behind to reach it.
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

        Assert.Contains(body, i => i is Copy { Src: Constant { Value: 1 }, Dst: Variable { Name: var n } } && n.EndsWith("GPIOR1", StringComparison.Ordinal));
        Assert.DoesNotContain(body, i => i is JumpIfZero or JumpIfNotZero);
    }
}
