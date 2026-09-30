using System.Linq;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// P2 AVR gaps bundle, item 5: float('inf') / float('nan'), math.isnan/isinf/isfinite, and
/// NaN comparison semantics.
///
/// float("inf")/float("nan") used to refuse ("not a number") -- double.TryParse's Float
/// style only recognises the exact tokens "Infinity"/"NaN", not CPython's case-insensitive
/// "inf"/"infinity"/"nan" with an optional sign. The target representation was never the
/// problem: _f32_repr (uart_text.py) has read the IEEE-754 exponent/mantissa bit pattern to
/// print "inf"/"nan" since it was written.
///
/// Two real bugs found while closing the value through to a working program:
///  - System.Text.Json refuses +-Infinity/NaN as numbers by default, so every .mir the
///    frontend handed a backend failed to serialize once a FloatConstant held one
///    (IrSerializer.cs, JsonNumberHandling.AllowNamedFloatingPointLiterals).
///  - The AVR backend's float `>`/`>=` used GCC's __cmpsf2 for every comparison; __cmpsf2
///    answers "greater" (0x01) for an unordered (NaN) operand, which >/>= then read as a
///    genuine "greater than" -- `nan > 1.0` answered True instead of CPython's False.
///    __gtsf2/__gesf2 are libgcc's own routines for those two operators, with the opposite
///    (correct) unordered answer.
///  - A SEPARATE bug in the IR generator's `if`/`while` jump optimizer: the jump-to-else
///    case negated a comparison by swapping to the algebraically opposite operator
///    (NOT(a&lt;b) == a&gt;=b), true for every ORDERED pair but false for NaN (both a&lt;b
///    and a&gt;=b are false), so `if n > 1.0:` on a NaN n took the THEN branch instead of
///    CPython's else -- a control-flow bug the boolean-VALUE fix above does not cover,
///    because if/while never materialises the comparison as a 0/1 value in the first place.
/// </summary>
public class FloatInfNanTests
{
    private const string Prelude =
        "from pymcu.chips.atmega328p import GPIOR0\n" +
        "from pymcu.types import uint8\n" +
        "\n" +
        "def main() -> None:\n";

    private static ProgramIR Gen(string body) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(Prelude + body).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });

    // --- Literal parsing ---

    [Theory]
    [InlineData("inf", double.PositiveInfinity)]
    [InlineData("+inf", double.PositiveInfinity)]
    [InlineData("-inf", double.NegativeInfinity)]
    [InlineData("Infinity", double.PositiveInfinity)]
    [InlineData("INFINITY", double.PositiveInfinity)]
    [InlineData("-infinity", double.NegativeInfinity)]
    public void FloatOfAnInfinitySpelling_ParsesToTheSignedInfinity(string text, double expected)
    {
        var ir = Gen($"    y: float = float('{text}')\n");
        var copy = Main(ir).OfType<Copy>().Single(c => c.Src is FloatConstant);
        Assert.Equal(expected, ((FloatConstant)copy.Src).Value);
    }

    [Theory]
    [InlineData("nan")]
    [InlineData("NaN")]
    [InlineData("-nan")]
    public void FloatOfANanSpelling_ParsesToNan(string text)
    {
        var ir = Gen($"    y: float = float('{text}')\n");
        var copy = Main(ir).OfType<Copy>().Single(c => c.Src is FloatConstant);
        Assert.True(double.IsNaN(((FloatConstant)copy.Src).Value));
    }

    [Fact]
    public void NegatedFloatInfinity_IsNegativeInfinity()
    {
        var ir = Gen("    y: float = -float('inf')\n");
        var copy = Main(ir).OfType<Copy>().Single(c => c.Src is FloatConstant);
        Assert.Equal(double.NegativeInfinity, ((FloatConstant)copy.Src).Value);
    }

    private static List<Instruction> Main(ProgramIR ir) =>
        ir.Functions.Single(f => f.Name == "main").Body;

    // --- The if/while NaN-safe jump fix ---

    [Fact]
    public void AGreaterThanConditionInAnIf_TakesTheDirectFloatJump_NotTheSwappedOperator()
    {
        // The regression: the else-jump used to swap `>` for `<=` (JumpIfLessOrEqual),
        // which silently answers a different question for a NaN operand. The fix takes
        // the direct sense (JumpIfGreaterThan) to a skip label instead.
        var ir = Gen(
            "    a: uint8 = GPIOR0.value\n" +
            "    x: float = float(a) / 3.0\n" +
            "    if x > 1.0:\n" +
            "        y: uint8 = 1\n" +
            "    else:\n" +
            "        y2: uint8 = 2\n");
        var body = Main(ir);
        Assert.Contains(body, i => i is JumpIfGreaterThan);
        Assert.DoesNotContain(body, i => i is JumpIfLessOrEqual);
    }

    [Fact]
    public void AGreaterOrEqualConditionInAnIf_TakesTheDirectFloatJump_NotTheSwappedOperator()
    {
        var ir = Gen(
            "    a: uint8 = GPIOR0.value\n" +
            "    x: float = float(a) / 3.0\n" +
            "    if x >= 1.0:\n" +
            "        y: uint8 = 1\n" +
            "    else:\n" +
            "        y2: uint8 = 2\n");
        var body = Main(ir);
        Assert.Contains(body, i => i is JumpIfGreaterOrEqual);
        Assert.DoesNotContain(body, i => i is JumpIfLessThan);
    }

    [Fact]
    public void ALessThanConditionInAnIf_TakesTheDirectFloatJump_NotTheSwappedOperator()
    {
        var ir = Gen(
            "    a: uint8 = GPIOR0.value\n" +
            "    x: float = float(a) / 3.0\n" +
            "    if x < 1.0:\n" +
            "        y: uint8 = 1\n" +
            "    else:\n" +
            "        y2: uint8 = 2\n");
        var body = Main(ir);
        Assert.Contains(body, i => i is JumpIfLessThan);
        Assert.DoesNotContain(body, i => i is JumpIfGreaterOrEqual);
    }

    // The INTEGER path is unaffected: no unordered case exists for an int compare, so the
    // single-jump swapped-operator form (the cheaper one) stays exactly as it was.
    [Fact]
    public void AnIntegerComparisonInAnIf_StillUsesTheSwappedOperator()
    {
        var ir = Gen(
            "    a: uint8 = GPIOR0.value\n" +
            "    if a > 10:\n" +
            "        y: uint8 = 1\n" +
            "    else:\n" +
            "        y2: uint8 = 2\n");
        var body = Main(ir);
        Assert.Contains(body, i => i is JumpIfLessOrEqual);
        Assert.DoesNotContain(body, i => i is JumpIfGreaterThan);
    }
}
