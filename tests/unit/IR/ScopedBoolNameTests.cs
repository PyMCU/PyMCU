using FluentAssertions;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// Bool-name tracking used to be a single program-wide pair of sets: any binding of a
/// name anywhere -- including a parameter inside an imported library routine -- vetoed
/// the name everywhere. Once `import a.b` runs the parent package first, the HAL's
/// `pwm` module loads, its `pwm_init_raw(off: ...)` parameter entered the program-wide
/// nonBool set, and the user's own `off = False` printed `0` instead of `False`
/// (fstring-bool). Bindings now veto only within their own scope.
/// </summary>
public class ScopedBoolNameTests
{
    private const string Prelude =
        "from pymcu.types import uint8\n" +
        "def uart_write_str(s: const[str]):\n" +
        "    pass\n" +
        "def uart_write_decimal_u8(v: uint8):\n" +
        "    pass\n";

    private static ProgramNode Parse(string src) =>
        new Parser(new Lexer(src).Tokenize()).ParseProgram();

    private static ProgramIR Gen(string src, Dictionary<string, ProgramNode> mods)
    {
        var config = new DeviceConfig { Arch = "avr", Chip = "atmega328p" };
        var program = Parse(Prelude + src);
        new ConditionalCompilator(config).Process(program);
        return new IRGenerator().Generate(program, mods, config);
    }

    private static bool Calls(Instruction i, string name) =>
        i is Call c && c.FunctionName == name;

    [Fact]
    public void ALibraryParameter_DoesNotVetoTheProgramsBoolLocal()
    {
        // The shape `import pymcu.hal` produces: an imported module whose routine
        // takes a parameter spelled like the user's bool local.
        var mods = new Dictionary<string, ProgramNode>
        {
            ["pwm"] = Parse("from pymcu.types import uint8\ndef pwm_init_raw(off: uint8):\n    pass\n"),
        };

        var main = Gen(
            "import pwm\n" +
            "def main():\n" +
            "    off = False\n" +
            "    print(off)\n", mods).Functions.Single(f => f.Name == "main");

        main.Body.Any(i => Calls(i, "uart_write_decimal_u8")).Should().BeFalse(
            "a library's `off` parameter is a different binding -- the local stays bool");
        main.Body.Any(i => Calls(i, "uart_write_str")).Should().BeTrue(
            "the bool stream writes the True/False flash words");
    }

    [Fact]
    public void ALocalThatLaterTurnsNumeric_StillPrintsAsANumber()
    {
        // Scoping must not weaken the veto within one function: `off` is bool at one
        // point and an integer later, so it keeps printing as a number everywhere.
        var mods = new Dictionary<string, ProgramNode>
        {
            ["pwm"] = Parse("from pymcu.types import uint8\ndef pwm_init_raw(off: uint8):\n    pass\n"),
        };

        var main = Gen(
            "import pwm\n" +
            "def get() -> uint8:\n" +
            "    return 3\n" +
            "def main():\n" +
            "    off = False\n" +
            "    off = get() - 3\n" +
            "    print(off)\n", mods).Functions.Single(f => f.Name == "main");

        main.Body.OfType<Call>().Any(c => c.FunctionName.StartsWith("uart_write_decimal"))
            .Should().BeTrue("bool at one point, integer later: the name prints as a number");
    }

    [Fact]
    public void AModuleLevelNonBool_StillVetoesProgramWide()
    {
        // Module-level bindings share one flat namespace: `pwm`'s module-level
        // `off = 0` still vetoes the entry file's module-level `off = False`.
        var mods = new Dictionary<string, ProgramNode>
        {
            ["pwm"] = Parse("from pymcu.types import uint8\noff = 0\ndef tick() -> uint8:\n    return off\n"),
        };

        var ir = Gen(
            "import pwm\n" +
            "off = False\n" +
            "print(off)\n" +
            "def main():\n" +
            "    pass\n", mods);

        ir.Functions.SelectMany(f => f.Body)
            .Any(i => Calls(i, "uart_write_decimal_u8")).Should().BeTrue(
            "a module-level integer binding vetoes the same name program-wide");
    }
}
