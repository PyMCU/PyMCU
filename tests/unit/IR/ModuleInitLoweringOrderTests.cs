using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// A module's synthesized `__module_init` is LOWERED before the functions of that module,
/// and emitted where it was.
///
/// Running the module level (accc7aab) made the construction happen; it did not decide WHEN
/// it is lowered, and lowering it is what binds a module-level instance's fields as
/// compile-time constants. The init is appended to the end of the compile list, so a
/// function of the same module was lowered first and saw run-time values -- which is why a
/// module-level Pin still failed from a function of its own module, on the field read
/// instead of on the missing construction (issue #117).
/// </summary>
public class ModuleInitLoweringOrderTests
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

    private const string TasksModule =
        "class Config:\n" +
        "    def __init__(self, base: uint8):\n" +
        "        self.base = base\n" +
        "\n" +
        "    def value(self) -> uint8:\n" +
        "        return self.base + 1\n" +
        "\n" +
        "cfg = Config(10)\n" +
        "\n" +
        "def read() -> uint8:\n" +
        "    return cfg.value()\n";

    private const string MainSrc =
        "from tasks import read\n" +
        "\n" +
        "def main():\n" +
        "    read()\n";

    [Fact]
    public void TheInitIsStillEmittedAfterTheModulesOwnFunctions()
    {
        var ir = Gen(MainSrc, ("tasks", TasksModule));

        int read = ir.Functions.FindIndex(f => f.Name == "tasks_read");
        int init = ir.Functions.FindIndex(f => f.Name == "tasks___module_init");

        Assert.True(read >= 0 && init >= 0);
        Assert.True(read < init, "hoisting is for LOWERING order only; emission order is unchanged");
    }

    // `Config.value()` reads and writes nothing, so since #175 the field it reads stays a
    // compile-time constant and the init has no storage to write. What #117 is about is that
    // the CONSTRUCTOR'S VALUE reaches the module's own function, so that is what is asserted;
    // whether it arrives folded or through a global is the optimisation, not the property.
    // The mutable path is covered by TheFieldAMethodWritesIsStillGivenStorage below.
    [Fact]
    public void TheConstructorsValue_ReachesItsModulesFunction()
    {
        var ir = Gen(MainSrc, ("tasks", TasksModule));

        var read = Assert.Single(ir.Functions, f => f.Name == "tasks_read");
        var call = Assert.Single(read.Body.OfType<Call>());
        var arg = Assert.Single(call.Args);

        Assert.True(arg is Constant { Value: 10 } or Variable { Name: "tasks_cfg_base" },
            $"the base the constructor was given must reach cfg.value(), got {arg}");
    }

    // The half that needs storage: a class one of whose methods assigns to a field. The fold
    // has to be given up there, or the write goes to a name with nothing behind it and the
    // reader sees the constructor's value -- issues #124 and #127.
    [Fact]
    public void TheFieldAMethodWritesIsStillGivenStorage()
    {
        var ir = Gen(
            "from tasks import bump, read\n\ndef main():\n    bump()\n    read()\n",
            ("tasks",
             "class Config:\n" +
             "    def __init__(self, base: uint8):\n" +
             "        self.base = base\n" +
             "\n" +
             "    def mark(self) -> None:\n" +
             "        self.base = 77\n" +
             "\n" +
             "    def value(self) -> uint8:\n" +
             "        return self.base + 1\n" +
             "\n" +
             "cfg = Config(10)\n" +
             "\n" +
             "def bump() -> None:\n" +
             "    cfg.mark()\n" +
             "\n" +
             "def read() -> uint8:\n" +
             "    return cfg.value()\n"));

        var init = Assert.Single(ir.Functions, f => f.Name == "tasks___module_init");
        Assert.Contains(init.Body, i =>
            i is Copy { Src: Constant { Value: 10 }, Dst: Variable { Name: "tasks_cfg_base" } });

        var read = Assert.Single(ir.Functions, f => f.Name == "tasks_read");
        var call = Assert.Single(read.Body.OfType<Call>());
        Assert.Equal("tasks_cfg_base", Assert.IsType<Variable>(Assert.Single(call.Args)).Name);
    }

    // The MicroPython and CircuitPython shape: no `def main():` at all. Running an imported
    // module's module level was wired to the explicit-main branch only, so this shape read
    // every module-level value of its imported modules as zero and said nothing.
    [Fact]
    public void ATopLevelScriptEntry_AlsoRunsItsImportedModulesModuleLevel()
    {
        var ir = Gen("from tasks import read\n\nread()\n", ("tasks", TasksModule));

        var init = Assert.Single(ir.Functions, f => f.Name == "tasks___module_init");
        Assert.Contains(init.Body, i =>
            i is Copy { Src: Constant { Value: 10 }, Dst: Variable { Name: "tasks_cfg_base" } });

        var main = Assert.Single(ir.Functions, f => f.Name == "main");
        int callInit = main.Body.FindIndex(i => i is Call { FunctionName: "tasks___module_init" });
        int callRead = main.Body.FindIndex(i => i is Call { FunctionName: "tasks_read" });
        Assert.True(callInit >= 0 && callRead >= 0 && callInit < callRead,
            "an import runs before the file that imports it");
    }

    [Fact]
    public void AProgramWithNoModuleInit_KeepsItsFunctionsInSourceOrder()
    {
        var ir = Gen(
            "from plain import twice\n\ndef main():\n    twice(2)\n",
            ("plain", "def twice(v: uint8) -> uint8:\n    return v * 2\n"));

        Assert.DoesNotContain(ir.Functions, f => f.Name.EndsWith("__module_init"));
        Assert.True(ir.Functions.FindIndex(f => f.Name == "plain_twice")
                  < ir.Functions.FindIndex(f => f.Name == "main"));
    }
}
