using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU#324. A parameter bound by KEYWORD has to clear the None an earlier expansion of the
/// same @inline function left on it. The parameter's key is the inline prefix plus its name,
/// and every expansion at the same depth reuses that key; the positional binding loop clears
/// it and the keyword one did not. So any call that let `parity` default to None made the
/// NEXT call's `parity=Parity.EVEN` read as None, the `match parity:` inside took the
/// `case None` arm, and a UART asked for 7E2 was programmed 7N2 with nothing said.
/// </summary>
public class KeywordArgumentNoneRebindTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    private const string Uart =
        "from pymcu.types import uint8, inline\n" +
        "from pymcu.chips.atmega328p import GPIOR0\n" +
        "\n" +
        "class UART:\n" +
        "    @inline\n" +
        "    def __init__(self, bits: uint8 = 8, parity = None):\n" +
        "        match parity:\n" +
        "            case None:\n" +
        "                GPIOR0.value = bits\n" +
        "            case _:\n" +
        "                GPIOR0.value = bits + parity * 16\n" +
        "\n";

    /// <summary>The constants `main` writes to the register, in order.</summary>
    private static List<int> RegisterWrites(ProgramIR ir)
    {
        var main = ir.Functions.Last(f => f.Name == "main");
        return main.Body.OfType<Copy>()
            .Where(c => c.Dst is Variable v && v.Name.EndsWith("GPIOR0", StringComparison.Ordinal))
            .Select(c => c.Src is Constant k ? k.Value : -1)
            .ToList();
    }

    [Fact]
    public void AnEarlierDefaultedCall_DoesNotMakeTheKeywordArgumentNone()
    {
        var ir = Gen(Uart +
            "def second():\n" +
            "    v = UART(8)\n" +
            "    return 0\n" +
            "\n" +
            "def main():\n" +
            "    u = UART(bits=7, parity=1)\n");

        // 7 + 1 * 16, and nothing else: a constant parity is never None, so the `case None`
        // arm is dead. Before the fix `main` carried that arm alone and the only write was 7.
        Assert.Equal(new List<int> { 23 }, RegisterWrites(ir));
    }

    [Fact]
    public void AConstantSubject_IsNeverNone_SoTheNoneArmIsDead()
    {
        var ir = Gen(Uart + "def main():\n    u = UART(bits=7, parity=1)\n");
        Assert.Equal(new List<int> { 23 }, RegisterWrites(ir));
    }

    [Fact]
    public void AKeywordNoneStillDecidesTheNoneArm()
    {
        var ir = Gen(Uart +
            "def second():\n" +
            "    v = UART(9, 2)\n" +
            "    return 0\n" +
            "\n" +
            "def main():\n" +
            "    u = UART(bits=7, parity=None)\n");

        // The other direction: a keyword None after a call that bound a value must still be
        // None, so only the `case None` arm survives.
        Assert.Equal(new List<int> { 7 }, RegisterWrites(ir));
    }
}
