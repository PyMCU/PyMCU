using FluentAssertions;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// `seq.index(x)` on a compile-time sequence -- a tuple/list literal or a name bound to
/// one. adafruit_tcs34725's gain setter writes `self._write_u8(_REGISTER_CONTROL,
/// _GAINS.index(val))` over the module-level `_GAINS = (1, 4, 16, 60)`: the receiver
/// resolved to a constant tuple but no method lowering owned the call, so it fell through
/// to a mangled `adafruit_tcs34725__GAINS_index` that does not exist.
///
/// A needle that folds yields a constant position; a run-time needle lowers to a
/// first-match compare chain whose miss arm raises ValueError.
/// </summary>
public class ConstSeqIndexTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });

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

    /// <summary>The value a `x = &lt;the call&gt;` statement delivers to `x`.</summary>
    private static Val AssignedTo(ProgramIR ir, string suffix) =>
        ir.Functions.SelectMany(f => f.Body).OfType<Copy>()
            .Where(c => c.Dst is Variable v && v.Name.EndsWith(suffix, StringComparison.Ordinal))
            .Select(c => c.Src)
            .Last();

    [Fact]
    public void ATupleLiteral_FoldsAConstantNeedle()
    {
        var ir = Gen("def main():\n    x = (1, 4, 16, 60).index(16)\n");

        AssignedTo(ir, "main.x").Should().BeEquivalentTo(new Constant(2),
            because: "16 sits at position 2 of the literal");
    }

    [Fact]
    public void ANameBoundTuple_FoldsAConstantNeedle()
    {
        var ir = Gen("_GAINS = (1, 4, 16, 60)\n" +
                     "def main():\n    x = _GAINS.index(60)\n");

        AssignedTo(ir, "main.x").Should().BeEquivalentTo(new Constant(3));
    }

    [Fact]
    public void TheFirstMatchWins()
    {
        var ir = Gen("_V = (1, 4, 4)\n" +
                     "def main():\n    x = _V.index(4)\n");

        AssignedTo(ir, "main.x").Should().BeEquivalentTo(new Constant(1),
            because: "index() answers the FIRST position that equals the needle");
    }

    [Fact]
    public void ARuntimeNeedle_LowersToACompareChainEndingInValueError()
    {
        var ir = Gen("from pymcu.chips.atmega328p import GPIOR0\n" +
                     "from pymcu.types import uint8\n" +
                     "_GAINS = (1, 4, 16, 60)\n" +
                     "def main():\n" +
                     "    val: uint8 = GPIOR0.value\n" +
                     "    x = _GAINS.index(val)\n");

        var main = ir.Functions.Should().ContainSingle(f => f.Name == "main").Which;
        main.Body.OfType<JumpIfNotEqual>().Count(j =>
                j.Src1 is Variable v && v.Name.EndsWith(".val", StringComparison.Ordinal))
            .Should().Be(4, because: "one compare per element of the tuple");
        main.Body.Should().Contain(i => i is SignalError,
            because: "the miss arm raises ValueError");
    }

    [Fact]
    public void AConstantMiss_RaisesValueError()
    {
        var ir = Gen("_GAINS = (1, 4, 16, 60)\n" +
                     "def main():\n    x = _GAINS.index(99)\n");

        ir.Functions.SelectMany(f => f.Body).Should().Contain(i => i is SignalError,
            because: "a miss the compiler can see still raises at runtime");
    }

    [Fact]
    public void AModuleLevelTuple_ResolvesInsideTheModulesOwnMethod()
    {
        // The measured shape: _GAINS is a global of the DRIVER's module, read bare inside
        // one of its methods -- the tcs34725 gain setter.
        const string sensorMod =
            "from pymcu.types import uint8\n\n" +
            "_GAINS = (1, 4, 16, 60)\n\n" +
            "class Sensor:\n" +
            "    def __init__(self):\n" +
            "        self._gain = 0\n\n" +
            "    def set_gain(self, val: uint8) -> None:\n" +
            "        self._gain = _GAINS.index(val)\n";

        var ir = GenWithModule(
            "from pymcu.types import uint8\n" +
            "import sensormod\n\n" +
            "def main():\n" +
            "    s = sensormod.Sensor()\n" +
            "    s.set_gain(16)\n",
            "sensormod", sensorMod);

        // Whether the setter is inlined or outlined, a compare against each element --
        // or the folded position -- is what the call must produce; what it must NOT
        // produce is a call to a mangled free function.
        ir.Functions.SelectMany(f => f.Body).OfType<Call>()
            .Should().NotContain(c => c.FunctionName.Contains("_GAINS_index"),
                because: "the sequence receiver owns .index -- no free function exists");
    }
}
