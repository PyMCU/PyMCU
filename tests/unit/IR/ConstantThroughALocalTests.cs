using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU#327. A call argument that holds a compile-time constant binds the callee's parameter
/// as that constant, not as a variable that happens to contain it.
///
/// Before, the difference was only whether the argument mentioned a local: `f(int(s * 1000))`
/// bound a constant and `x = int(s * 1000); f(x)` bound a variable with the same constant in
/// it. Every callee that DISPATCHES on the value -- the calibrated delay loops,
/// pwm_prescaler_for_freq, claim(), any `match` on a const parameter -- lost its constant path
/// as soon as the caller held the value in a local first: measured at 60 bytes more and a
/// 974 us delay where 1000 us was asked for. A `const` parameter did not merely lose the path,
/// it refused the call.
/// </summary>
public class ConstantThroughALocalTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    private const string Prelude =
        "from pymcu.types import uint8, uint16, inline, const\n" +
        "from pymcu.chips.atmega328p import GPIOR0\n" +
        "\n" +
        "@inline\n" +
        "def pick(n: const[uint8]):\n" +
        "    match n:\n" +
        "        case 1:\n" +
        "            GPIOR0.value = 11\n" +
        "        case 2:\n" +
        "            GPIOR0.value = 22\n" +
        "        case _:\n" +
        "            GPIOR0.value = 99\n" +
        "\n" +
        "@inline\n" +
        "def plain(n: uint8):\n" +
        "    match n:\n" +
        "        case 1:\n" +
        "            GPIOR0.value = 11\n" +
        "        case 2:\n" +
        "            GPIOR0.value = 22\n" +
        "        case _:\n" +
        "            GPIOR0.value = 99\n" +
        "\n";

    /// <summary>The constants written to the register, in order.</summary>
    private static List<int> RegisterWrites(ProgramIR ir)
    {
        var main = ir.Functions.Last(f => f.Name == "main");
        return main.Body.OfType<Copy>()
            .Where(c => c.Dst is Variable v && v.Name.EndsWith("GPIOR0", StringComparison.Ordinal))
            .Select(c => c.Src is Constant k ? k.Value : -1)
            .ToList();
    }

    [Fact]
    public void APlainLocalCarriesTheConstantIntoAConstParameter()
    {
        var ir = Gen(Prelude + "def main():\n    x = 2\n    pick(x)\n");
        Assert.Equal(new List<int> { 22 }, RegisterWrites(ir));
    }

    [Fact]
    public void AnAnnotatedLocalCarriesItToo()
    {
        var ir = Gen(Prelude + "def main():\n    x: uint8 = 2\n    pick(x)\n");
        Assert.Equal(new List<int> { 22 }, RegisterWrites(ir));
    }

    [Fact]
    public void AnOrdinaryParameterDispatchesOnItAsWell()
    {
        var ir = Gen(Prelude + "def main():\n    x: uint8 = 1\n    plain(x)\n");
        Assert.Equal(new List<int> { 11 }, RegisterWrites(ir));
    }

    [Fact]
    public void TheLocalAndTheExpressionLowerTheSame()
    {
        var direct = RegisterWrites(Gen(Prelude + "def main():\n    plain(uint8(1 + 1))\n"));
        var viaLocal = RegisterWrites(Gen(Prelude + "def main():\n    y = 1 + 1\n    plain(y)\n"));
        Assert.Equal(direct, viaLocal);
    }

    [Fact]
    public void AValueTheProgramReallyDecidesAtRunTimeStaysRunTime()
    {
        var ir = Gen(Prelude +
            "def main():\n" +
            "    x: uint8 = GPIOR0.value\n" +
            "    plain(x)\n");
        // Every arm is still lowered: nothing was decided here.
        var writes = RegisterWrites(ir);
        Assert.Contains(11, writes);
        Assert.Contains(22, writes);
        Assert.Contains(99, writes);
    }

    [Fact]
    public void ANameTheLoopReassignsIsNotTakenAsAConstant()
    {
        // `n` is 1 before the loop and something else inside it. The call is lowered once for
        // every iteration, so binding the first iteration's value would be wrong for the rest.
        var ir = Gen(Prelude +
            "def main():\n" +
            "    n: uint8 = 1\n" +
            "    while GPIOR0.value:\n" +
            "        plain(n)\n" +
            "        n = n + 1\n");
        var writes = RegisterWrites(ir);
        Assert.Contains(11, writes);
        Assert.Contains(22, writes);
        Assert.Contains(99, writes);
    }
}
