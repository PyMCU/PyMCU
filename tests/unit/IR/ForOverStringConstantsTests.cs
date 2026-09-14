using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU#308. A `for` over a short constant list unrolls, and the loop variable is a
/// compile-time constant in each iteration -- which is exactly what a `const` pin parameter
/// needs. The unroller accepted integers only, so the CircuitPython idiom for a row of pins,
/// `for pin in (board.D2, board.D3, board.D4)`, was refused with "elements must be
/// compile-time integer constants" for elements that ARE compile-time constants, and every
/// guide with more than one pin had to be written out one call per pin.
/// </summary>
public class ForOverStringConstantsTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    private const string Prelude =
        "from pymcu.types import uint8, const, inline\n" +
        "from pymcu.chips.atmega328p import GPIOR0\n" +
        "\n" +
        "@inline\n" +
        "def bit_of(name: const):\n" +
        "    match name:\n" +
        "        case \"PD2\":\n" +
        "            GPIOR0.value = 2\n" +
        "        case \"PD3\":\n" +
        "            GPIOR0.value = 3\n" +
        "        case \"PD4\":\n" +
        "            GPIOR0.value = 4\n" +
        "        case _:\n" +
        "            GPIOR0.value = 0\n" +
        "\n" +
        "D2 = \"PD2\"\n" +
        "D3 = \"PD3\"\n" +
        "D4 = \"PD4\"\n" +
        "\n";

    private static List<int> RegisterWrites(ProgramIR ir)
    {
        var main = ir.Functions.Last(f => f.Name == "main");
        return main.Body.OfType<Copy>()
            .Where(c => c.Dst is Variable v && v.Name.EndsWith("GPIOR0", StringComparison.Ordinal))
            .Select(c => c.Src is Constant k ? k.Value : -1)
            .ToList();
    }

    [Fact]
    public void AListOfStringLiterals_Unrolls()
    {
        var ir = Gen(Prelude +
            "def main():\n" +
            "    for p in [\"PD2\", \"PD3\", \"PD4\"]:\n" +
            "        bit_of(p)\n");
        Assert.Equal(new List<int> { 2, 3, 4 }, RegisterWrites(ir));
    }

    [Fact]
    public void ATupleOfNamedStringConstants_Unrolls()
    {
        // The board-pin spelling: each element is a module constant, not a literal.
        var ir = Gen(Prelude +
            "def main():\n" +
            "    for p in (D2, D3, D4):\n" +
            "        bit_of(p)\n");
        Assert.Equal(new List<int> { 2, 3, 4 }, RegisterWrites(ir));
    }

    [Fact]
    public void PairsOfAStringAndANumber_Unroll()
    {
        var ir = Gen(Prelude +
            "def main():\n" +
            "    for p, n in [(D2, 7), (D3, 8)]:\n" +
            "        bit_of(p)\n" +
            "        GPIOR0.value = n\n");
        Assert.Equal(new List<int> { 2, 7, 3, 8 }, RegisterWrites(ir));
    }

    [Fact]
    public void AnElementThatIsNotAConstant_IsStillRefused()
    {
        var ex = Assert.ThrowsAny<PyMCU.Common.CompilerError>(() => Gen(Prelude +
            "def main():\n" +
            "    v: uint8 = GPIOR0.value\n" +
            "    for p in [1, v]:\n" +
            "        GPIOR0.value = p\n"));
        Assert.Contains("compile-time constants", ex.Message);
    }
}
