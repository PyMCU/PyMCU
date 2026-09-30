using System.Linq;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// P2 AVR gaps bundle, item 2: round(x, n) with n a compile-time constant. Previously
/// rejected outright ("round() is a Python builtin that PyMCU does not provide"). An
/// integer x keeps CPython's own int semantics and folds at compile time; a float x
/// (constant or run-time) forwards to pymcu.round2's _pymcu_round2, half-to-even on the
/// EXACT decimal expansion of the float32 value -- the same algorithm the f-string float
/// format spec already uses.
/// </summary>
public class Round2Tests
{
    private const string Prelude =
        "from pymcu.types import uint8, uint16, ptr\n" +
        "def uart_write_str(s: const[str]):\n" +
        "    pass\n" +
        "def uart_write(c: uint8):\n" +
        "    pass\n" +
        "def uart_write_decimal_u8(v: uint8):\n" +
        "    pass\n" +
        "GPIOR0: ptr[uint8] = ptr(0x3E)\n";

    private static readonly Dictionary<string, ProgramNode> Round2Module = new()
    {
        ["pymcu.round2"] = new Parser(new Lexer(
            "from pymcu.types import uint8, uint32, int32\n" +
            "def _pymcu_round2(value: float, n: int32) -> float:\n" +
            "    return value\n").Tokenize()).ParseProgram(),
    };

    private static ProgramIR Gen(string src, Dictionary<string, ProgramNode>? modules = null) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(Prelude + src).Tokenize()).ParseProgram(),
            modules ?? new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    private static List<Call> Calls(ProgramIR ir) =>
        ir.Functions.SelectMany(f => f.Body).OfType<Call>().ToList();

    // --- Integer x: folds directly, no helper needed ---

    [Fact]
    public void RoundOfAnIntWithNonNegativeN_IsUnchanged()
    {
        var ir = Gen("print(round(5, 2))\n");
        Assert.DoesNotContain(Calls(ir), c => c.FunctionName.Contains("_pymcu_round2"));
    }

    [Theory]
    [InlineData(1234, -2, 1200)]
    [InlineData(1250, -2, 1200)]   // tie: 12.5 -> even 12
    [InlineData(1150, -2, 1200)]   // tie: 11.5 -> even 12
    [InlineData(-1250, -2, -1200)]
    public void RoundOfAnIntWithNegativeN_RoundsHalfToEven(int x, int n, int expected)
    {
        var ir = Gen($"print(round({x}, {n}))\n");
        var writes = ir.Functions.SelectMany(f => f.Body).OfType<Call>()
            .Where(c => c.FunctionName.Contains("uart_write_decimal")).ToList();
        // The folded Constant feeds the decimal writer directly; assert via the constant
        // operand rather than re-deriving CPython's answer a second time here.
        var copies = ir.Functions.SelectMany(f => f.Body).OfType<Copy>()
            .Where(c => c.Src is Constant).Select(c => ((Constant)c.Src).Value).ToList();
        Assert.Contains(expected, copies);
    }

    // --- Float x: forwards to pymcu.round2 ---

    [Fact]
    public void RoundOfAFloatConstant_CallsRound2()
    {
        var ir = Gen(
            "import pymcu.round2 as _pymcu_round2\n" +
            "print(round(3.14159, 2))\n",
            Round2Module);
        Assert.Contains(Calls(ir), c => c.FunctionName.Contains("_pymcu_round2"));
    }

    [Fact]
    public void RoundOfARuntimeFloat_CallsRound2()
    {
        var ir = Gen(
            "import pymcu.round2 as _pymcu_round2\n" +
            "def show(x: float):\n" +
            "    print(round(x, 2))\n" +
            "show(GPIOR0.value / 7.0)\n",
            Round2Module);
        Assert.Contains(Calls(ir), c => c.FunctionName.Contains("_pymcu_round2"));
    }

    [Fact]
    public void RoundOfAFloat_WithoutTheInjectedModule_RefusesByName()
    {
        // pymcu build injects pymcu.round2 automatically; invoking the compiler directly
        // without it (as this test does on purpose) must name the gap, not crash.
        var ex = Assert.Throws<PyMCU.Common.CompilerError>(() => Gen(
            "def show(x: float):\n" +
            "    print(round(x, 2))\n" +
            "show(GPIOR0.value / 7.0)\n"));
        Assert.Contains("pymcu.round2", ex.Message);
    }

    // --- n must be a compile-time constant ---

    [Fact]
    public void RoundWithARuntimeNdigits_RefusesNamingTheArgument()
    {
        var ex = Assert.Throws<PyMCU.Common.CompilerError>(() => Gen(
            "def show(x: float, n: uint8):\n" +
            "    print(round(x, n))\n" +
            "show(GPIOR0.value / 7.0, GPIOR0.value)\n"));
        Assert.Contains("ndigits", ex.Message);
        Assert.Contains("compile-time constant", ex.Message);
    }

    [Fact]
    public void RoundWithNdigitsOutOfRange_Refuses()
    {
        var ex = Assert.Throws<PyMCU.Common.CompilerError>(() => Gen(
            "print(round(3.14, 99))\n"));
        Assert.Contains("out of the supported range", ex.Message);
    }
}
