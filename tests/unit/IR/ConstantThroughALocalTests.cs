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

    // The program #327 was opened for: a value computed into locals, narrowed by a cast, and
    // handed to a callee that dispatches on it. `nap` and `nap_spelled` are the same arithmetic
    // written two ways, and they have to lower the same -- the spelled-out one folds through
    // the AST evaluator, the one with locals did not, and the difference was a generic counted
    // delay against the calibrated loop.
    private const string NapPrelude =
        "from pymcu.types import uint16, uint32, inline\n" +
        "from pymcu.chips.atmega328p import GPIOR0\n" +
        "\n" +
        "def sink(ms: uint16):\n" +
        "    GPIOR0.value = uint16(ms)\n" +
        "\n" +
        "@inline\n" +
        "def shim(ms: uint16):\n" +
        "    sink(ms)\n" +
        "\n" +
        "@inline\n" +
        "def nap(seconds: float):\n" +
        "    total_us: uint32 = uint32(seconds * 1000000.0 + 0.5)\n" +
        "    ms: uint32 = total_us // 1000\n" +
        "    if ms != 0:\n" +
        "        shim(uint16(ms))\n" +
        "\n" +
        "@inline\n" +
        "def nap_spelled(seconds: float):\n" +
        "    if uint32(seconds * 1000000.0 + 0.5) // 1000 != 0:\n" +
        "        shim(uint16(uint32(seconds * 1000000.0 + 0.5) // 1000))\n" +
        "\n";

    /// <summary>
    /// What the callee's parameter is given, across every call in main. A temporary is
    /// followed back to the Copy that defined it: both spellings stage through one, and what
    /// separates them is whether that staging copy carries a constant.
    /// </summary>
    private static List<string> ParameterSources(ProgramIR ir)
    {
        var body = ir.Functions.Last(f => f.Name == "main").Body;

        string Describe(Val src)
        {
            for (int hop = 0; hop < 4 && src is Temporary tmp; ++hop)
            {
                var def = body.OfType<Copy>().LastOrDefault(
                    c => c.Dst is Temporary d && d.Name == tmp.Name);
                if (def == null) break;
                src = def.Src;
            }
            return src switch
            {
                Constant k => "const " + k.Value,
                Temporary => "tmp",
                Variable v2 => "var " + v2.Name,
                _ => "other",
            };
        }

        return body.OfType<Copy>()
            .Where(c => c.Dst is Variable v && v.Name == "sink.ms")
            .Select(c => Describe(c.Src))
            .ToList();
    }

    [Fact]
    public void AValueComputedIntoLocals_ReachesTheCalleeAsTheConstantItIs()
    {
        var ir = Gen(NapPrelude + "def main():\n    nap(0.001)\n");
        // 1 ms: the cast narrows a uint32 that holds 1, which fits, so the callee is handed 1.
        Assert.Equal(new List<string> { "const 1" }, ParameterSources(ir));
    }

    [Fact]
    public void TheTwoSpellingsGiveTheCalleeTheSameThing()
    {
        var viaLocals = ParameterSources(Gen(NapPrelude + "def main():\n    nap(0.001)\n"));
        var spelled = ParameterSources(Gen(NapPrelude + "def main():\n    nap_spelled(0.001)\n"));
        Assert.Equal(spelled, viaLocals);
    }

    [Fact]
    public void ACastThatWouldTruncate_StaysRunTime()
    {
        // 70000 does not fit the uint16 the cast narrows to, so the number the callee would see
        // is not the one the folder returns. Left to the run-time path.
        var ir = Gen(NapPrelude +
            "@inline\n" +
            "def big(seconds: float):\n" +
            "    total_us: uint32 = uint32(seconds * 1000000.0)\n" +
            "    shim(uint16(total_us))\n" +
            "\n" +
            "def main():\n" +
            "    big(0.07)\n");
        Assert.DoesNotContain("const 70000", ParameterSources(ir));
    }

    [Fact]
    public void ANameTheBranchesDisagreeOn_IsNotTakenAsAConstant()
    {
        // Each arm assigns a different value, so past the chain the name holds neither. Kept
        // separate because the arms are lowered in order and the last one would otherwise be
        // the answer: a PWM duty came out 0x3F where 0x7F was asked for.
        var ir = Gen(Prelude +
            "def main():\n" +
            "    n: uint8 = 0\n" +
            "    if GPIOR0.value:\n" +
            "        n = 1\n" +
            "    else:\n" +
            "        n = 2\n" +
            "    plain(n)\n");
        var writes = RegisterWrites(ir);
        Assert.Contains(11, writes);
        Assert.Contains(22, writes);
        Assert.Contains(99, writes);
    }

    [Fact]
    public void ANameAnAugmentedAssignmentChanged_IsNotTakenAsAConstant()
    {
        // `total = 0` then `total += ...`: the write is augmented, and it is still a write.
        // Taken as 0, a sum printed 0 for every list.
        var ir = Gen(Prelude +
            "def main():\n" +
            "    total: uint8 = 0\n" +
            "    total += GPIOR0.value\n" +
            "    plain(total)\n");
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
