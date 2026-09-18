using FluentAssertions;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU#381. <c>alarm.py</c> names its module-level singleton <c>time</c> so
/// CircuitPython's <c>alarm.time.TimeAlarm</c> spelling works. A user's own
/// <c>import time</c> against that name can file <c>alarm</c> as an instance,
/// and member reads then refused <c>alarm.time</c> as "object has no attribute
/// 'time'". The singleton is a module member; the hop has to survive the
/// collision.
/// </summary>
[Trait("Issue", "381")]
public class AlarmTimeModuleMemberTests
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

    private const string TimeMod =
        "from pymcu.types import uint8\n" +
        "def monotonic() -> uint8:\n" +
        "    return 0\n";

    private const string AlarmMod =
        "from pymcu.types import uint8, inline\n" +
        "class _TimeAlarmModule:\n" +
        "    class TimeAlarm:\n" +
        "        @inline\n" +
        "        def __init__(self, monotonic_time: uint8 = 0):\n" +
        "            self._deadline = monotonic_time\n" +
        "time = _TimeAlarmModule()\n" +
        "def sleep_until_alarms(alarm0) -> uint8:\n" +
        "    return 0\n";

    [Fact]
    public void AlarmTimeTimeAlarm_CompilesAlongsideImportTime()
    {
        var ir = Gen(
            "from pymcu.chips.atmega328p import GPIOR0\n" +
            "import time\n" +
            "import alarm\n" +
            "ta = alarm.time.TimeAlarm(monotonic_time=0)\n" +
            "GPIOR0.value = alarm.sleep_until_alarms(ta)\n",
            ("time", TimeMod),
            ("alarm", AlarmMod));

        ir.Functions.Should().Contain(f => f.Name == "main",
            because: "alarm.time.TimeAlarm must lower after a user's own import time");
    }
}
