using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU#376. An annotation naming an ENUM MEMBER, not the enum type -- `Direction.OUTPUT`,
/// module-qualified as `digitalio.Direction.OUTPUT` -- stopped at "unknown type" even though
/// `Direction` is a class this compiler knows, because the check that reads a dotted
/// annotation as a class name only tried the LAST segment as the class itself and refused
/// once it also failed as a member.
///
/// CircuitPython's digitalio exposes Direction.OUTPUT and Pull.UP as the values a property
/// can take, and a driver annotates the property with the value it actually returns -- unusual
/// Python, and legal. `Direction` here is a plain class with only ALL-CAPS attributes (the
/// same shape CircuitPython itself uses instead of the `enum` module), so the fix is judged
/// against the class the annotation names, the same loose way the existing `busio.I2C`
/// dotted-class check already is.
/// </summary>
public class EnumMemberAnnotationTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });

    private static ProgramIR GenWithModule(string mainSrc, string moduleName, string moduleSrc)
    {
        var imported = new Dictionary<string, ProgramNode>
        {
            [moduleName] = new Parser(new Lexer(moduleSrc).Tokenize()).ParseProgram(),
        };
        var mainAst = new Parser(new Lexer(mainSrc).Tokenize()).ParseProgram();
        var ctx = new PyMCU.Common.CompilationContext(new CompilerOptions(
            FilePath: "main.py", OutputPath: "", Arch: "avr", Target: "atmega328p",
            Frequency: 16000000, Configs: [], Includes: [], ResetVector: 0, InterruptVector: 0,
            Verbose: false));
        ctx.ProjectModules.Add(moduleName);
        return new IRGenerator().Generate(mainAst, imported, new DeviceConfig { Arch = "avr" },
                                          projectModules: ctx.ProjectModules);
    }

    private const string DirectionClass =
        "class Direction:\n" +
        "    INPUT = 0\n" +
        "    OUTPUT = 1\n\n";

    [Fact]
    public void ABareEnumMemberAnnotationOnAPropertyBuilds()
    {
        // The issue's minimal shape, without the module qualifier.
        var ir = Gen(DirectionClass +
            "class ExpanderPin:\n" +
            "    def __init__(self, n: int):\n" +
            "        self._n: int = n\n\n" +
            "    @property\n" +
            "    def direction(self) -> Direction.OUTPUT:\n" +
            "        return Direction.OUTPUT\n\n" +
            "p = ExpanderPin(3)\n" +
            "x = p.direction\n");

        Assert.Contains(ir.Functions, f => f.Name == "main");
    }

    [Fact]
    public void AModuleQualifiedEnumMemberAnnotationBuilds()
    {
        // `digitalio.Direction.OUTPUT` -- the exact spelling adafruit_74hc595 and
        // adafruit_pcf8574 stop on. A class reached through a real module import, matching
        // how the compat layer is actually imported (not a bare or nested-class stand-in).
        const string digitalioMod =
            "class Direction:\n" +
            "    INPUT = 0\n" +
            "    OUTPUT = 1\n";

        var ir = GenWithModule(
            "import digitalio\n\n" +
            "class ExpanderPin:\n" +
            "    def __init__(self, n: int):\n" +
            "        self._n: int = n\n\n" +
            "    @property\n" +
            "    def direction(self) -> digitalio.Direction.OUTPUT:\n" +
            "        return digitalio.Direction.OUTPUT\n\n" +
            "p = ExpanderPin(3)\n" +
            "x = p.direction\n",
            "digitalio", digitalioMod);

        Assert.Contains(ir.Functions, f => f.Name == "main");
    }

    [Fact]
    public void TheAnnotationDoesNotChangeWhatTheComparisonFoldsTo()
    {
        // Not just "it builds" -- the property's value has to still BE Direction.OUTPUT.
        // `p.direction` always returns Direction.OUTPUT, so `== Direction.OUTPUT` is always
        // true and the compiler folds the whole branch away; the false arm must not survive.
        var ir = Gen(DirectionClass +
            "class ExpanderPin:\n" +
            "    def __init__(self, n: int):\n" +
            "        self._n: int = n\n\n" +
            "    @property\n" +
            "    def direction(self) -> Direction.OUTPUT:\n" +
            "        return Direction.OUTPUT\n\n" +
            "p = ExpanderPin(3)\n" +
            "if p.direction == Direction.OUTPUT:\n" +
            "    x: int = 7\n" +
            "else:\n" +
            "    x: int = 9\n");

        var consts = ir.Functions.SelectMany(f => f.Body).OfType<Copy>()
            .Where(c => c.Src is Constant).Select(c => ((Constant)c.Src).Value).ToList();
        Assert.Contains(7, consts);
        Assert.DoesNotContain(9, consts);
    }
}
