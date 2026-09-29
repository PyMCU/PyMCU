using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// pow() and divmod() with the operands CPython gives them.
///
/// pow() of two integers with a run-time argument forwarded both to the float routine
/// __pymcu_powf, whose parameters are floats: it received two integer bit patterns and
/// every such call printed 1.0. It is integer exponentiation, the operation `**` already
/// lowers.
///
/// divmod() floors like // and %, and its two results carry the division's width and sign.
/// The fold truncated toward zero (divmod(-17, 5) gave -3, -2), and `q, r = divmod(...)`
/// sized both targets uint8 because the result slots were never registered, so a run-time
/// -143 printed as 113.
/// </summary>
public class BuiltinArithmeticTests
{
    private const string Prelude =
        "from pymcu.types import int16, uint8\n" +
        "from pymcu.chips.atmega328p import GPIOR0\n" +
        "def uart_write_str(s: const[str]):\n" +
        "    pass\n" +
        "def uart_write_decimal_u8(v: uint8):\n" +
        "    pass\n" +
        "def uart_write_decimal_u16(v: uint16):\n" +
        "    pass\n";

    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(Prelude + src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });

    private static List<Instruction> Main(ProgramIR ir) =>
        ir.Functions.Single(f => f.Name == "main").Body;

    [Fact]
    public void PowOfARuntimeInteger_IsIntegerExponentiation()
    {
        var ir = Gen(
            "b = GPIOR0.value + 3\n" +
            "r = pow(b, 5)\n");

        Assert.DoesNotContain(Main(ir), i => i is Call c && c.FunctionName == "__pymcu_powf");
        Assert.Equal(4, Main(ir).OfType<Binary>().Count(b => b.Op == PyMCU.IR.BinaryOp.Mul));
    }

    [Fact]
    public void PowWithARuntimeIntegerExponent_IsRefused()
    {
        var ex = Assert.ThrowsAny<Exception>(() => Gen(
            "s = GPIOR0.value\n" +
            "r = pow(3, s + 5)\n"));
        Assert.Contains("exponent must be a compile-time constant integer", ex.Message);
    }

    [Fact]
    public void DivmodOfNegativeLiterals_Floors()
    {
        var ir = Gen(
            "q, r = divmod(-17, 5)\n" +
            "print(q)\n" +
            "print(r)\n");

        var q = Main(ir).OfType<Copy>().Single(c => c.Dst is Variable { Name: "main.q" });
        var r = Main(ir).OfType<Copy>().Single(c => c.Dst is Variable { Name: "main.r" });
        Assert.True(((Variable)q.Dst).Type.IsSigned());
        Assert.Equal(-4, (q.Src as Constant)?.Value ?? ConstOf(ir, q.Src));
        Assert.Equal(3, (r.Src as Constant)?.Value ?? ConstOf(ir, r.Src));
    }

    private static int ConstOf(ProgramIR ir, Val v) =>
        ((Constant)Main(ir).OfType<Copy>().Single(c => c.Dst == v).Src).Value;

    [Fact]
    public void DivmodUnpack_TargetsTakeTheDivisionsWidth()
    {
        var ir = Gen(
            "a: int16 = GPIOR0.value - 1000\n" +
            "q, r = divmod(a, GPIOR0.value + 7)\n" +
            "print(q)\n" +
            "print(r)\n");

        var q = Main(ir).OfType<Copy>().Single(c => c.Dst is Variable { Name: "main.q" });
        Assert.Equal(DataType.INT16, ((Variable)q.Dst).Type);
    }

    // ---- divmod() zero-divisor and bare-value bugs found by the float-edges campaign -------
    //
    // EmitDivmodBuiltin used to build its Binary(FloorDiv)/Binary(Mod) nodes directly instead
    // of going through the checked binary-expression codegen that / // % use, so a divisor the
    // compiler could not fold to a constant skipped the zero-check entirely: `q, r =
    // divmod(1.0, x)` with a run-time x == 0.0 silently answered (inf, nan)-shaped garbage
    // instead of raising ZeroDivisionError. And when the call was not unpacked into exactly two
    // targets -- `v = divmod(a, b)`, `print(divmod(a, b))` -- it silently answered the
    // QUOTIENT ALONE, dropping the remainder with no diagnostic, contradicting the documented
    // "returns (quotient, remainder)".

    [Fact]
    public void DivmodWithARuntimeIntegerZeroDivisor_Raises()
    {
        var ir = Gen(
            "b: uint8 = GPIOR0.value\n" +
            "q, r = divmod(17, b)\n" +
            "print(q)\n");

        // A raise directly in main with no enclosing try reaches __pymcu_unhandled_exn
        // through a SignalError whose CatchLabel is the landing right before that call
        // (see UnhandledRaiseInMainTests) -- CatchLabel == null is the OTHER, broken form
        // (SET; RET with no caller), so the halt call itself is what to assert on.
        Assert.Contains(Main(ir).OfType<SignalError>(), s => s.Code is Constant { Value: 6 });
        Assert.Contains(Main(ir).OfType<Call>(), c => c.FunctionName == "__pymcu_unhandled_exn");
    }

    [Fact]
    public void DivmodWithARuntimeFloatZeroDivisor_Raises()
    {
        var ir = Gen(
            "x: float = float(GPIOR0.value)\n" +
            "q, r = divmod(1.0, x)\n" +
            "print(q)\n");

        Assert.Contains(Main(ir).OfType<SignalError>(), s => s.Code is Constant { Value: 6 });
        Assert.Contains(Main(ir).OfType<Call>(), c => c.FunctionName == "__pymcu_unhandled_exn");
    }

    [Fact]
    public void DivmodOfAConstantZeroDivisor_IsRefusedAtCompileTime()
    {
        Assert.Contains("divmod(): division by zero",
            Assert.ThrowsAny<Exception>(() => Gen("q, r = divmod(17, 0)\n")).Message);
        Assert.Contains("divmod(): division by zero",
            Assert.ThrowsAny<Exception>(() => Gen("q, r = divmod(1.0, 0.0)\n")).Message);
    }

    [Theory]
    [InlineData("v = divmod(17, 5)\n")]
    [InlineData("print(divmod(17, 5))\n")]
    public void ABareDivmodResult_IsRefused(string body)
    {
        string msg = Assert.ThrowsAny<Exception>(() => Gen(body)).Message;
        Assert.Contains("divmod() returns a 2-tuple", msg);
        Assert.Contains("q, r = divmod(a, b)", msg);
    }
}
