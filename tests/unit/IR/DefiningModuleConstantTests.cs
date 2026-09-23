using FluentAssertions;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// Two modules declare a class of the same bare name with different class constants:
/// the HAL's <c>Pin</c> carries IN=1/OUT=0, the compat layer's <c>machine.Pin</c>
/// carries the upstream values IN=0/OUT=1 and translates at the HAL call. A bare
/// <c>Pin.IN</c> inside a machine.py body is machine's own binding -- but the member
/// read fell through the flat globals to the CALLER's import table, and only
/// `from machine import Pin` happened to keep a `Pin` entry there. Under
/// `import machine` (or `import machine as m`, or `from machine import Pin as P`)
/// the read reached classModuleMap, whose bare-name entry is whichever module
/// scanned last -- the HAL's -- so `machine.Pin(13, machine.Pin.OUT)` built an
/// input. The defining module's namespace must answer first.
/// </summary>
public class DefiningModuleConstantTests
{
    private static ProgramIR Gen(string mainSrc, params (string Name, string Source)[] modules)
    {
        var imported = new Dictionary<string, ProgramNode>();
        foreach (var (name, source) in modules)
            imported[name] = new Parser(new Lexer(source).Tokenize()).ParseProgram();

        var mainAst = new Parser(new Lexer(mainSrc).Tokenize()).ParseProgram();
        var ctx = new PyMCU.Common.CompilationContext(new CompilerOptions(
            FilePath: "main.py", OutputPath: "", Arch: "avr", Target: "atmega328p",
            Frequency: 16000000, Configs: [], Includes: [], ResetVector: 0, InterruptVector: 0,
            Verbose: false));
        foreach (var (name, _) in modules) ctx.ProjectModules.Add(name);

        return new IRGenerator().Generate(mainAst, imported, new DeviceConfig { Arch = "avr" },
                                          projectModules: ctx.ProjectModules);
    }

    private static List<int> RegisterWrites(ProgramIR ir)
    {
        var main = ir.Functions.Last(f => f.Name == "main");
        return main.Body.OfType<Copy>()
            .Where(c => c.Dst is Variable v && v.Name.EndsWith("GPIOR0", StringComparison.Ordinal))
            .Select(c => c.Src is Constant k ? k.Value : -1)
            .ToList();
    }

    private const string Prelude =
        "from pymcu.types import uint8, const\n" +
        "from pymcu.chips.atmega328p import GPIOR0\n" +
        "\n";

    // The HAL's direction convention: input is 1, output is 0.
    private const string Hal =
        "from pymcu.types import uint8, const\n" +
        "class Pin:\n" +
        "    IN = 1\n" +
        "    OUT = 0\n";

    // The compat layer carries upstream values (IN=0, OUT=1) and translates once
    // at the HAL call: mode==Pin.IN -> HAL 1, mode==Pin.OUT -> HAL 0.
    private const string Machine =
        "from pymcu.types import uint8, const\n" +
        "class Pin:\n" +
        "    IN = 0\n" +
        "    OUT = 1\n" +
        "    def __init__(self, mode: const):\n" +
        "        self.m = 1 if mode == Pin.IN else (0 if mode == Pin.OUT else mode)\n";

    private static void WritesHalOut(string mainSrc)
    {
        // hal is listed second so its `class Pin` overwrites classModuleMap["Pin"],
        // matching a real build where the compat module is scanned before the HAL
        // it wraps.
        var ir = Gen(Prelude + mainSrc, ("machine", Machine), ("hal", Hal));
        Assert.Equal(new List<int> { 0 }, RegisterWrites(ir));
    }

    [Fact]
    public void DottedModuleCall_ResolvesTheDefiningModulesPin()
    {
        WritesHalOut(
            "import machine\n" +
            "def main():\n" +
            "    p = machine.Pin(machine.Pin.OUT)\n" +
            "    GPIOR0.value = p.m\n");
    }

    [Fact]
    public void AliasedModuleCall_ResolvesTheDefiningModulesPin()
    {
        WritesHalOut(
            "import machine as m\n" +
            "def main():\n" +
            "    p = m.Pin(m.Pin.OUT)\n" +
            "    GPIOR0.value = p.m\n");
    }

    [Fact]
    public void FromImportCall_ResolvesTheDefiningModulesPin()
    {
        WritesHalOut(
            "from machine import Pin\n" +
            "def main():\n" +
            "    p = Pin(Pin.OUT)\n" +
            "    GPIOR0.value = p.m\n");
    }

    [Fact]
    public void AliasedFromImportCall_ResolvesTheDefiningModulesPin()
    {
        WritesHalOut(
            "from machine import Pin as P\n" +
            "def main():\n" +
            "    p = P(P.OUT)\n" +
            "    GPIOR0.value = p.m\n");
    }

    [Fact]
    public void LiteralMode_TakesTheSamePath()
    {
        // Literal 1 means OUT upstream and must translate to HAL 0 exactly like
        // Pin.OUT does.
        WritesHalOut(
            "import machine\n" +
            "def main():\n" +
            "    p = machine.Pin(1)\n" +
            "    GPIOR0.value = p.m\n");
    }
}
