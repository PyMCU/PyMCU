using FluentAssertions;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU#467. <c>from adafruit_motor import servo</c> followed by
/// <c>servo = servo.Servo(pwm)</c> is the spelling the Adafruit servo guide
/// uses. The assignment rebinds <c>servo</c> to a Servo instance, but member
/// reads kept resolving the name as the module alias:
/// <c>print(servo.fraction)</c> was <c>Unknown module member:
/// adafruit_motor_servo_fraction</c>, and <c>servo.set_pulse_width_range</c>
/// mangled to an undefined free function. Writes already saw the instance.
///
/// The unit tests use <c>import thing</c> then <c>thing = thing.Thing()</c>,
/// which is the same table: a name in <c>modules</c> rebound to an instance.
/// The AVR fixture uses the guide's <c>from motor import servo</c>.
/// </summary>
[Trait("Issue", "467")]
public class ReboundModuleAliasTests
{
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

    private const string ThingMod =
        "from pymcu.types import uint8\n" +
        "class Thing:\n" +
        "    def __init__(self) -> None:\n" +
        "        self.x: uint8 = 0\n" +
        "    def get(self) -> uint8:\n" +
        "        return self.x\n";

    private const string Main =
        "from pymcu.chips.atmega328p import GPIOR0, GPIOR1\n" +
        "import thing\n" +
        "thing = thing.Thing()\n" +
        "thing.x = 7\n" +
        "GPIOR0.value = thing.x\n" +
        "GPIOR1.value = thing.get()\n";

    [Fact]
    public void AReadThroughAReboundModuleAliasSeesTheInstance()
    {
        var ir = GenWithModule(Main, "thing", ThingMod);

        ir.Functions.Should().Contain(f => f.Name == "main",
            because: "the program must compile after rebinding the import alias");

        var stores = ir.Functions.SelectMany(f => f.Body).OfType<Copy>()
            .Where(c => c.Dst is Variable v && v.Name.EndsWith("GPIOR0", StringComparison.Ordinal))
            .Select(c => c.Src)
            .ToList();
        stores.Should().NotBeEmpty(
            because: "GPIOR0.value = thing.x must lower after the rebind");
        stores.Should().AllSatisfy(s =>
        {
            var name = (s as Variable)?.Name ?? "";
            name.Should().Contain("thing_x",
                because: "thing.x after thing = thing.Thing() is the instance field, not a module member");
            name.Should().NotContain("m_thing",
                because: "the read must not mangle through the import alias as a module global");
        });
    }

    [Fact]
    public void AMethodCallThroughAReboundModuleAliasSeesTheInstance()
    {
        var ir = GenWithModule(Main, "thing", ThingMod);

        ir.Functions.Should().Contain(f => f.Name == "main",
            because: "thing.get() after the rebind must compile, not mangle to thing_get");

        var stores = ir.Functions.SelectMany(f => f.Body).OfType<Copy>()
            .Where(c => c.Dst is Variable v && v.Name.EndsWith("GPIOR1", StringComparison.Ordinal))
            .ToList();
        stores.Should().NotBeEmpty(
            because: "GPIOR1.value = thing.get() must lower as an instance method, not a module function");
    }
}
