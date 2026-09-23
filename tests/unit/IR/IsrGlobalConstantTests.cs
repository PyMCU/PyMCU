using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// A name an ISR's `global` statements declare is written between any two instructions of
/// the code around it, so the value a watched store gave it is provable nowhere -- not
/// inside the handler's own body either. The entry file building its object at module
/// level is exactly what lowers `main` ahead of the handler, so the handler's
/// `if _armed == 0:` saw the clear's store, folded, and its arm's `return` discarded the
/// whole recording tail as dead -- the pulse capture that never records a pulse.
/// </summary>
public class IsrGlobalConstantTests
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

    private const string PulseMod =
        "from pymcu.types import uint8, const, inline, compile_isr\n" +
        "\n" +
        "_armed: uint8 = 0\n" +
        "_seen:  uint8 = 0\n" +
        "\n" +
        "class Cap:\n" +
        "    def __init__(self):\n" +
        "        global _armed\n" +
        "        _armed = 0\n" +
        "\n" +
        "def pulse_isr():\n" +
        "    global _armed, _seen\n" +
        "    if _armed == 0:\n" +
        "        _armed = 1\n" +
        "        return\n" +
        "    _seen = 1\n" +
        "\n" +
        "@inline\n" +
        "def attach():\n" +
        "    compile_isr(pulse_isr, 0x0002)\n";

    // `o = Cap()` at module level is what moves main's lowering ahead of pulse_isr's.
    private const string Main =
        "import pulsemod\n" +
        "o = pulsemod.Cap()\n" +
        "pulsemod.attach()\n";

    [Fact]
    public void IsrGlobalWrittenBeforeRegistration_DoesNotFoldTheHandlersTest()
    {
        var isr = Assert.Single(Gen(Main, ("pulsemod", PulseMod))
                              .Functions.Where(f => f.IsInterrupt));

        // The comparison must stay a runtime branch: without the registration-time kill the
        // arm emitted unconditionally, and `_seen = 1` -- the whole tail -- never lowered.
        Assert.Contains(isr.Body, i => i is JumpIfNotEqual { Src1: Variable { Name: "pulsemod__armed" } });
        Assert.Contains(isr.Body, i => i is Copy { Src: Constant { Value: 1 },
                                                 Dst: Variable { Name: "pulsemod__seen" } });
    }
}
