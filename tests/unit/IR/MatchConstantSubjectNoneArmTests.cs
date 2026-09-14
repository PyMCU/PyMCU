using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU#324, second half. A `match` subject that folded to a compile-time constant is never
/// None, so a `case None` arm cannot match it. The arm was lowered anyway, as a run-time
/// comparison against a value that has no representation, and the register it happened to read
/// decided which arm ran. `busio.UART(parity=Parity.EVEN)` chose between "no parity" and the
/// parity it was given that way.
/// </summary>
public class MatchConstantSubjectNoneArmTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    private const string Prelude =
        "from pymcu.types import uint8, inline\n" +
        "from pymcu.chips.atmega328p import GPIOR0\n" +
        "\n" +
        "@inline\n" +
        "def frame(parity = None):\n" +
        "    match parity:\n" +
        "        case None:\n" +
        "            GPIOR0.value = 1\n" +
        "        case 5:\n" +
        "            GPIOR0.value = 2\n" +
        "        case _:\n" +
        "            GPIOR0.value = 3\n" +
        "\n";

    private static List<int> RegisterWrites(ProgramIR ir)
    {
        var main = ir.Functions.Last(f => f.Name == "main");
        return main.Body.OfType<Copy>()
            .Where(c => c.Dst is Variable v && v.Name.EndsWith("GPIOR0", StringComparison.Ordinal))
            .Select(c => c.Src is Constant k ? k.Value : -1)
            .ToList();
    }

    private static bool ComparesAgainstNone(ProgramIR ir) =>
        ir.Functions.Last(f => f.Name == "main").Body
            .OfType<Binary>().Any(b => b.Src1 is NoneVal || b.Src2 is NoneVal);

    [Fact]
    public void AConstantThatMatchesNoArm_ReachesTheWildcardAlone()
    {
        var ir = Gen(Prelude + "def main():\n    frame(7)\n");
        Assert.Equal(new List<int> { 3 }, RegisterWrites(ir));
        Assert.False(ComparesAgainstNone(ir));
    }

    [Fact]
    public void AConstantThatMatchesALaterArm_SelectsItAlone()
    {
        var ir = Gen(Prelude + "def main():\n    frame(5)\n");
        Assert.Equal(new List<int> { 2 }, RegisterWrites(ir));
        Assert.False(ComparesAgainstNone(ir));
    }

    [Fact]
    public void ANoneSubject_StillSelectsTheNoneArm()
    {
        var ir = Gen(Prelude + "def main():\n    frame()\n");
        Assert.Equal(new List<int> { 1 }, RegisterWrites(ir));
    }
}
