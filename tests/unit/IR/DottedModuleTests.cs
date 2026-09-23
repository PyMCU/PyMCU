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
}
