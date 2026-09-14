using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU#321. Any HAL module that owns an interrupt has to put it on a pin, and the pin table
/// lives in the GPIO module. Both halves of writing that failed.
///
/// A function named as a VALUE was looked for under the bare name alone, and functions are
/// registered under the prefix of the module that defines them, so the handler was reported
/// undefined in the very file that defines it, forty lines below the definition.
///
/// And composing the vector out of the pin table was refused, because an @inline function
/// whose body is a `match` over const arms returning literals hands back the slot its value
/// was folded into rather than a bare literal. The workaround was to write the pin-to-vector
/// table out a second time, in the module that has the handler.
/// </summary>
public class CrossModuleIsrHandlerTests
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

    // The GPIO module: it owns the pin-to-vector table and the registration.
    private const string GpioMod =
        "from pymcu.types import uint8, const, inline, compile_isr\n" +
        "from pymcu.chips.atmega328p import GPIOR0\n" +
        "\n" +
        "@inline\n" +
        "def pin_irq_vector(name: const) -> uint8:\n" +
        "    match name:\n" +
        "        case \"PD2\":\n" +
        "            return 0x0002\n" +
        "        case _:\n" +
        "            return 0x0004\n" +
        "\n" +
        "@inline\n" +
        "def pin_irq_setup(name: const, trigger: uint8, handler: const):\n" +
        "    GPIOR0.value = trigger\n" +
        "    compile_isr(handler, pin_irq_vector(name))\n";

    // The module that owns the interrupt, and never repeats the table.
    private const string PulseMod =
        "from pymcu.types import const, inline\n" +
        "from pymcu.chips.atmega328p import GPIOR1\n" +
        "from gpiomod import pin_irq_setup\n" +
        "\n" +
        "def pulse_isr():\n" +
        "    GPIOR1.value = 1\n" +
        "\n" +
        "@inline\n" +
        "def pulse_capture_attach(pin: const):\n" +
        "    pin_irq_setup(pin, 3, pulse_isr)\n";

    private const string Main =
        "from pulsemod import pulse_capture_attach\n" +
        "\n" +
        "def main():\n" +
        "    pulse_capture_attach(\"PD2\")\n";

    [Fact]
    public void AHandlerDefinedInTheCallersModule_IsRegisteredAtTheVector()
    {
        var ir = Gen(Main, ("gpiomod", GpioMod), ("pulsemod", PulseMod));

        var isr = Assert.Single(ir.Functions.Where(f => f.IsInterrupt));
        Assert.Equal("pulsemod_pulse_isr", isr.Name);
    }

    [Fact]
    public void TheVectorComesFromTheGpioModulesOwnTable()
    {
        var isr = Assert.Single(Gen(Main, ("gpiomod", GpioMod), ("pulsemod", PulseMod))
                                   .Functions.Where(f => f.IsInterrupt));
        Assert.Equal(0x0002, isr.InterruptVector);
    }

    [Fact]
    public void AnotherPinSelectsTheOtherArm()
    {
        var isr = Assert.Single(Gen(Main.Replace("\"PD2\"", "\"PD3\""),
                                    ("gpiomod", GpioMod), ("pulsemod", PulseMod))
                                   .Functions.Where(f => f.IsInterrupt));
        Assert.Equal(0x0004, isr.InterruptVector);
    }

    [Fact]
    public void ARunTimeVector_IsStillRefused()
    {
        const string Bad =
            "from pymcu.types import uint8, compile_isr\n" +
            "from pymcu.chips.atmega328p import GPIOR0\n" +
            "\n" +
            "def h():\n" +
            "    GPIOR0.value = 1\n" +
            "\n" +
            "def main():\n" +
            "    v: uint8 = GPIOR0.value\n" +
            "    compile_isr(h, v)\n";

        var ex = Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(Bad));
        Assert.Contains("compile-time constant", ex.Message);
    }
}
