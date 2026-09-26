using PyMCU.Common;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

// A comparison with a float on either side, and both values known, is decided at compile time.
//
// An `if` never lowers its test through VisitBinary: EmitOptimizedConditionalJump turns it
// into a conditional jump over the two values, and it folded only a pair of integer Constants.
// So `if x < 0.0:` over a `const[float]` parameter bound to -1.0 became a run-time
// `__cmpsf2`, and a `raise CompileError` under it was downgraded to "guard could not be
// verified": the refusal it was written for never fired. `keypad.Keys(interval=-1.0)` built.
// The same comparison assigned to a name, which does go through VisitBinary, was folded.
//
// And an integer literal against a run-time float reached the backend as an integer, which
// converted it with `__floatsisf` on every evaluation.
//
// WHAT DISCRIMINATES: the guard refusals, and the absence of a float compare in main.
// Against the unfixed compiler the guards build and main keeps a compare-and-jump.
//
// WHAT IS INVARIANT: a run-time float compared with a literal still compares at run time.
public class FloatConstantComparisonTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });

    private static List<Instruction> Main(ProgramIR ir) =>
        ir.Functions.Last(f => f.Name == "main").Body;

    private static bool IsCompareJump(Instruction i) =>
        i is JumpIfEqual or JumpIfNotEqual or JumpIfLessThan or JumpIfLessOrEqual
            or JumpIfGreaterThan or JumpIfGreaterOrEqual;

    private static IEnumerable<Val> Operands(Instruction i) => i switch
    {
        JumpIfEqual j => [j.Src1, j.Src2],
        JumpIfNotEqual j => [j.Src1, j.Src2],
        JumpIfLessThan j => [j.Src1, j.Src2],
        JumpIfLessOrEqual j => [j.Src1, j.Src2],
        JumpIfGreaterThan j => [j.Src1, j.Src2],
        JumpIfGreaterOrEqual j => [j.Src1, j.Src2],
        Binary b => [b.Src1, b.Src2],
        _ => []
    };

    private const string Preamble =
        "from pymcu.chips.atmega328p import GPIOR1\n" +
        "from pymcu.types import uint8, uint32, const, inline\n\n";

    private const string Check =
        "@inline\n" +
        "def check(x: const[float]):\n" +
        "    if x < 0.0:\n" +
        "        raise CompileError(\"negative\")\n" +
        "    GPIOR1.value = 7\n";

    [Fact]
    public void AGuardOverAConstFloatParameterRefusesTheCall()
    {
        var ex = Assert.Throws<ArchitectureError>(() => Gen(Preamble + Check + "check(-1.0)\n"));
        Assert.Contains("negative", ex.Message);
    }

    [Fact]
    public void AGuardAgainstAnIntegerLiteralRefusesTheCallToo()
    {
        var src = Preamble + Check.Replace("x < 0.0", "x < 0") + "check(-1.0)\n";
        var ex = Assert.Throws<ArchitectureError>(() => Gen(src));
        Assert.Contains("negative", ex.Message);
    }

    [Fact]
    public void AGuardThatHoldsLeavesNoCompareBehind()
    {
        var body = Main(Gen(Preamble + Check + "check(1.0)\n"));
        Assert.DoesNotContain(body, IsCompareJump);
        Assert.Contains(body, i => i is Copy { Src: Constant { Value: 7 } });
    }

    [Fact]
    public void AConstFloatExpressionComparedInAnIfIsDecided()
    {
        var body = Main(Gen(Preamble +
            "@inline\n" +
            "def sleep(seconds: const[float]):\n" +
            "    if seconds * 1000.0 > 65535.0:\n" +
            "        GPIOR1.value = 1\n" +
            "    else:\n" +
            "        GPIOR1.value = 2\n" +
            "sleep(100.0)\n" +
            "sleep(0.5)\n"));

        Assert.DoesNotContain(body, IsCompareJump);
        Assert.Contains(body, i => i is Copy { Src: Constant { Value: 1 } });
        Assert.Contains(body, i => i is Copy { Src: Constant { Value: 2 } });
    }

    [Fact]
    public void AnIntegerLiteralAgainstARunTimeFloatIsAFloatLiteral()
    {
        var body = Main(Gen(Preamble +
            "v: float = float(GPIOR1.value) - 3.5\n" +
            "if v < 0:\n" +
            "    GPIOR1.value = 1\n" +
            "b: bool = v > 2\n" +
            "GPIOR1.value = uint8(b)\n"));

        // Still a run-time comparison: v is not known.
        Assert.Contains(body, IsCompareJump);
        // Both literals reach the comparison as floats, which is what spares the chip the
        // `__floatsisf` it used to call to convert them.
        var operands = body.Where(i => IsCompareJump(i) || i is Binary).SelectMany(Operands).ToList();
        Assert.Contains(operands, o => o is FloatConstant { Value: 0.0 });
        Assert.Contains(operands, o => o is FloatConstant { Value: 2.0 });
    }
}
