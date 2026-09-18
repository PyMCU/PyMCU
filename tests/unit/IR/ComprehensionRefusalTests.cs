using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU#307. Three different programs reached one refusal and all three were told there is a
/// filter. `[DigitalInOut(p) for p in pins]` has no `if` in it, and the reader was sent to
/// look for one; what is unsupported there is a comprehension of INSTANCES, which have no
/// array slot to live in.
/// </summary>
public class ComprehensionRefusalTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    private const string Prelude =
        "from pymcu.types import uint8, inline\n" +
        "from pymcu.chips.atmega328p import GPIOR0\n" +
        "\n" +
        "class Led:\n" +
        "    @inline\n" +
        "    def __init__(self, pin: uint8):\n" +
        "        self._pin = pin\n" +
        "\n";

    // The comprehension this once refused is now expanded: each element is
    // constructed straight into its own `leds__k` slot, so `for l in leds`
    // resolves the class the way a literal of constructions does.
    [Fact]
    public void AComprehensionOfInstances_CompilesToSlotInstances()
    {
        var ir = Gen(Prelude +
            "def main():\n" +
            "    pins = [\"PD2\", \"PD3\", \"PD4\"]\n" +
            "    leds = [Led(p) for p in pins]\n" +
            "    GPIOR0.value = 1\n");

        Assert.NotNull(ir);
    }

    [Fact]
    public void AComprehensionThatReallyHasAFilter_StillSaysFilter()
    {
        var ex = Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(Prelude +
            "def main():\n" +
            "    xs = [i for i in range(8) if i > 2]\n" +
            "    GPIOR0.value = 1\n"));

        Assert.Contains("filter", ex.Message);
    }

    [Fact]
    public void AComprehensionWithNowhereToLive_SaysThat()
    {
        var ex = Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(Prelude +
            "def main():\n" +
            "    GPIOR0.value = [i for i in range(4)]\n"));

        Assert.Contains("fixed array", ex.Message);
        Assert.DoesNotContain("filter", ex.Message);
    }
}
