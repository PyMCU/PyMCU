using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU#372. The optional-import flag every CircuitPython driver opens with must fold, or the
/// library takes BOTH branches and compiles the one it does not use.
///
///     _USE_PULSEIO = False
///     try:
///         from pulseio import PulseIn
///         _USE_PULSEIO = True
///     except ImportError:
///         pass
///
/// Two causes, and the program needed both fixed. A boolean literal was not a constant
/// expression, so `FLAG = True` fell out of the ALL-CAPS constant path into the mutable one;
/// and two straight-line top-level writes counted as a reassignment, which is right for a name
/// written from inside functions and wrong for a module's top level, which runs once in order.
///
/// Measured on adafruit_hcsr04, whose `__init__` picks `PulseIn` or `DigitalInOut` for
/// `self._echo` on this flag: with the flag unfolded the field took two types and
/// `self._echo.clear()` compiled against the one that has no `clear`.
/// </summary>
public class ModuleFlagFoldsTests
{
    private const string Hdr = "from pymcu.chips.atmega328p import GPIOR0\n\n";

    private static ProgramIR Gen(string mainSrc, string modName, string modSrc)
    {
        var imported = new Dictionary<string, ProgramNode>
        {
            [modName] = new Parser(new Lexer(modSrc).Tokenize()).ParseProgram(),
        };
        var ctx = new PyMCU.Common.CompilationContext(new CompilerOptions(
            FilePath: "main.py", OutputPath: "", Arch: "avr", Target: "atmega328p",
            Frequency: 16000000, Configs: [], Includes: [], ResetVector: 0, InterruptVector: 0,
            Verbose: false));
        ctx.ProjectModules.Add(modName);
        return new IRGenerator().Generate(
            new Parser(new Lexer(mainSrc).Tokenize()).ParseProgram(), imported,
            new DeviceConfig { Arch = "avr" }, projectModules: ctx.ProjectModules);
    }

    private const string Main =
        Hdr +
        "from flagmod import FLAG\n\n" +
        "def main():\n" +
        "    if FLAG:\n" +
        "        GPIOR0.value = 1\n" +
        "    else:\n" +
        "        GPIOR0.value = 2\n";

    /// The branch that was NOT taken leaves no store behind, so one store means it folded.
    private static int Stores(ProgramIR ir) =>
        ir.Functions.SelectMany(f => f.Body).OfType<Copy>()
          .Count(c => c.Dst is Variable v && v.Name.EndsWith("GPIOR0"));

    [Fact]
    public void AnImportedBooleanConstantFolds()
    {
        Assert.Equal(1, Stores(Gen(Main, "flagmod", "FLAG = True\n")));
    }

    [Fact]
    public void TheFalseSpellingFoldsToItsOwnBranch()
    {
        var ir = Gen(Main, "flagmod", "FLAG = False\n");
        Assert.Equal(1, Stores(ir));
        Assert.Contains(ir.Functions.SelectMany(f => f.Body).OfType<Copy>(),
            c => c.Src is Constant k && k.Value == 2);
    }

    [Fact]
    public void TwoStraightLineTopLevelWritesFoldToTheLast()
    {
        var ir = Gen(Main, "flagmod", "FLAG = False\nFLAG = True\n");
        Assert.Equal(1, Stores(ir));
        Assert.Contains(ir.Functions.SelectMany(f => f.Body).OfType<Copy>(),
            c => c.Src is Constant k && k.Value == 1);
    }

    // The WHOLE idiom, with the `try` still standing, is not exercised here: whether an
    // optional import loaded is decided by the dependency graph builder, which this
    // single-source harness does not run, so the try never folds and its two writes are nested
    // rather than straight-line. It is measured end to end instead, on the same program
    // `pymcu build` compiles: one branch, 166 bytes, identical on both front ends -- and
    // adafruit_hcsr04 builds unmodified at 4 160 bytes, which it could not before.

    [Fact]
    public void ANameWrittenFromInsideAFunctionIsStillMutable()
    {
        // The rule this narrows is right for THIS program and must keep being right: a name a
        // function writes through `global` is a variable whose initializer merely happens to be
        // constant, so both branches have to be lowered. Written in the SAME module as the
        // name, which is where `global` reaches.
        var ir = Gen(Main, "flagmod",
            "FLAG = False\n" +
            "FLAG = True\n\n" +
            "def flip():\n" +
            "    global FLAG\n" +
            "    FLAG = False\n");
        Assert.Equal(2, Stores(ir));
    }

    [Fact]
    public void ATopLevelReadBeforeTheLastWriteKeepsTheNameMutable()
    {
        // Two writes with a read between them: a read before the last write sees a different
        // value than a read after it, and one constant cannot answer for both.
        var ir = Gen(Main, "flagmod",
            "from pymcu.chips.atmega328p import GPIOR1\n" +
            "FLAG = 1\n" +
            "GPIOR1.value = FLAG\n" +
            "FLAG = 0\n");
        Assert.Equal(2, Stores(ir));
    }
}
