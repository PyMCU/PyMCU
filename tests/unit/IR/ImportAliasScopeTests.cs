using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU#320. `from x import C as _C` binds a name in ONE file. The compiler kept one flat
/// table of import aliases shared by every module, so two files that aliased different things
/// to the same name got whichever was registered first: a wrapper class ended up constructing
/// itself, and the error named the one file that was correct.
/// </summary>
public class ImportAliasScopeTests
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

    private const string WidgetMod =
        "class Widget:\n" +
        "    def __init__(self):\n" +
        "        self.n = 1\n";

    // The wrapper aliases the low-level class out of the way, as busio.py does with the HAL.
    private const string BusMod =
        "from widget import Widget as _W\n" +
        "\n" +
        "class BusI2C:\n" +
        "    def __init__(self, scl: uint8, sda: uint8):\n" +
        "        self._w = _W()\n" +
        "        self._locked = scl\n";

    // The factory aliases the WRAPPER to the same name, which no Python file can see.
    private const string PinsMod =
        "from bus import BusI2C as _W\n" +
        "\n" +
        "def make() -> uint8:\n" +
        "    b = _W(5, 4)\n" +
        "    return b._locked\n";

    [Fact]
    public void TwoModulesAliasingTheSameName_EachKeepTheirOwn()
    {
        var ir = Gen("import pins\n\ndef main():\n    x: uint8 = pins.make()\n",
            ("bus", BusMod), ("widget", WidgetMod), ("pins", PinsMod));
        Assert.NotNull(ir);
    }

    [Fact]
    public void TheEntryFilesAliasIsNotTakenByAModule()
    {
        // main and the module both write `_W`, and they mean different classes.
        var ir = Gen("from widget import Widget as _W\n" +
                     "import pins\n" +
                     "\n" +
                     "def main():\n" +
                     "    w = _W()\n" +
                     "    x: uint8 = pins.make()\n",
            ("bus", BusMod), ("widget", WidgetMod), ("pins", PinsMod));
        Assert.NotNull(ir);
    }
}
