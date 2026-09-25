using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// `import alarm.time` binds `alarm`, and `alarm.time.X` resolves through the package
/// to the dotted module's own symbols -- the path the compat layer's alarm package
/// needs so `alarm.time.TimeAlarm` works the way upstream's submodules do.
/// </summary>
public class DottedModuleTests
{
    private static ProgramIR Gen(string src, Dictionary<string, ProgramNode> mods)
    {
        var config = new DeviceConfig { Arch = "avr", Chip = "atmega328p" };
        var program = new Parser(new Lexer(src).Tokenize()).ParseProgram();
        new ConditionalCompilator(config).Process(program);
        return new IRGenerator().Generate(program, mods, config);
    }

    private static ProgramNode Parse(string src) =>
        new Parser(new Lexer(src).Tokenize()).ParseProgram();

    private static bool Calls(ProgramIR ir, string mangled) =>
        ir.Functions.Any(f => f.Name == mangled) ||
        ir.Functions.SelectMany(f => f.Body).OfType<Call>().Any(c => c.FunctionName == mangled);

    [Fact]
    public void ImportAlarmTime_BindsThePackage_AndResolvesTheSubmoduleMember()
    {
        var mods = new Dictionary<string, ProgramNode>
        {
            ["alarm.time"] = Parse("def tick() -> uint8:\n    return 1\n"),
        };

        var ir = Gen(
            "import alarm.time\n" +
            "buf = bytearray(1)\n" +
            "buf[0] = alarm.time.tick()\n", mods);

        Assert.True(Calls(ir, "alarm_time_tick"),
            "alarm.time.tick() should resolve to the dotted module's function");
    }

    [Fact]
    public void PackageInitReExport_ImportAlarm_ResolvesTheSubmodule()
    {
        // compat-cp-alarm's shape: `import alarm` alone, while the package __init__
        // binds its submodule -- `from . import time` is rewritten by
        // RelativeImportResolver to `import alarm.time as time` before generation.
        // `alarm.time` must lower to the submodule's own name (alarm_time); the
        // re-export chase must not re-append the member and emit alarm_time_time.
        var mods = new Dictionary<string, ProgramNode>
        {
            ["alarm.time"] = Parse(
                "def tick() -> uint8:\n    return 1\n"),
            ["alarm"] = Parse(
                "import alarm.time as time\n"),
        };

        var ir = Gen(
            "import alarm\n" +
            "buf = bytearray(1)\n" +
            "buf[0] = alarm.time.tick()\n", mods);

        Assert.True(Calls(ir, "alarm_time_tick"),
            "alarm.time.tick() should resolve to the dotted module's function");
        Assert.False(Calls(ir, "alarm_time_time_tick"),
            "the re-export chase must not double-append the submodule member");
    }

    [Fact]
    public void AUserVariableSpelledLikeTheMangledMember_DoesNotStealTheSubmodule()
    {
        // `alarm_time` as a user global collides with `alarm`'s member `time` under
        // mod_member mangling: membership must come from the module's own scope.
        var mods = new Dictionary<string, ProgramNode>
        {
            ["alarm.time"] = Parse("def tick() -> uint8:\n    return 2\n"),
        };

        var ir = Gen(
            "import alarm.time\n" +
            "alarm_time = 99\n" +
            "buf = bytearray(1)\n" +
            "buf[0] = alarm.time.tick()\n", mods);

        Assert.True(Calls(ir, "alarm_time_tick"),
            "the user's alarm_time variable must not stand in for alarm.time");
    }

    [Fact]
    public void AnInlineBodyUnderAnAliasPrefix_SeesItsNonInlineSibling()
    {
        // One file imported under two names: `pymcu.time.delay_us` expands its inline
        // body under the pymcu_time_ prefix, where a call to the non-inline
        // _delay_us_avr resolved to nothing -- the sibling only exists under the
        // canonical time_ prefix functionModulePrefix never propagated.
        var timeAst = Parse(
            "def _delay_us_avr(us: uint8) -> uint8:\n    return us + 1\n" +
            "@inline\n" +
            "def delay_us(us: uint8) -> uint8:\n    return _delay_us_avr(us)\n");
        var mods = new Dictionary<string, ProgramNode>
        {
            ["time"] = timeAst,
            ["pymcu.time"] = timeAst,
        };

        var ir = Gen(
            "import pymcu.time\n" +
            "buf = bytearray(1)\n" +
            "buf[0] = pymcu.time.delay_us(4)\n", mods);

        Assert.True(Calls(ir, "time__delay_us_avr"),
            "the alias expansion should still reach the canonical module's helper");
    }

    [Fact]
    public void PackageInitReExportsAFunction_ImportPackage_CallsTheDefiningModule()
    {
        // #468. `pkg/__init__.py` doing `from pkg.mod import f` compiles f under the
        // DEFINING module, so `pkg.f()` has no pkg_f to reach. The member READ of the
        // same name already chased the re-export; the CALL reported it undefined.
        var mods = new Dictionary<string, ProgramNode>
        {
            ["pkg.mod"] = Parse("def twice(n: uint8) -> uint8:\n    return n * 2\n"),
            ["pkg"] = Parse("from pkg.mod import twice\n"),
        };

        var ir = Gen(
            "import pkg\n" +
            "buf = bytearray(1)\n" +
            "buf[0] = pkg.twice(3)\n", mods);

        Assert.True(Calls(ir, "pkg_mod_twice"),
            "pkg.twice() should reach the module that defines it");
        Assert.False(Calls(ir, "pkg_twice"),
            "no pkg_twice is ever emitted, so the call must not name it");
    }

    [Fact]
    public void AModuleThatDefinesTheFunctionItself_IsStillCalledUnderItsOwnName()
    {
        // The chase must not fire on a module that defines the name: a package whose
        // __init__ both re-exports a name AND defines one of its own keeps its own.
        var mods = new Dictionary<string, ProgramNode>
        {
            ["pkg.mod"] = Parse("def twice(n: uint8) -> uint8:\n    return n * 2\n"),
            ["pkg"] = Parse(
                "from pkg.mod import twice\n" +
                "def thrice(n: uint8) -> uint8:\n    return n * 3\n"),
        };

        var ir = Gen(
            "import pkg\n" +
            "buf = bytearray(1)\n" +
            "buf[0] = pkg.thrice(3)\n", mods);

        Assert.True(Calls(ir, "pkg_thrice"),
            "a function the package defines itself keeps the package's own name");
    }
}
