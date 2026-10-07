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

        // The divisor `GPIOR0.value + 7` is signed int16, so b = -1 is in range
        // and the quotient a / -1 can be 32768 -- past int16's top. The pair
        // takes the signed rank that holds it: int32.
        var q = Main(ir).OfType<Copy>().Single(c => c.Dst is Variable { Name: "main.q" });
        Assert.Equal(DataType.INT32, ((Variable)q.Dst).Type);
    }

    [Fact]
    public void DivmodOfMixedSignOperands_StoresSignedResults()
    {
        // The result type used to follow the WIDER operand -- size only, no sign --
        // so divmod(uint8, int8) divided signed but stored into a uint8 slot and
        // (-4, -3) printed as (252, 253). The quotient's worst case is a / -1 = -a
        // (one signed rank above the dividend's own), and the remainder of an
        // unsigned divisor is non-negative under b's maximum (one signed rank above
        // the divisor's own when the result is signed).
        var ir = Gen(
            "a: uint8 = GPIOR0.value + 17\n" +
            "b: int8 = int8(GPIOR0.value) - 5\n" +
            "q, r = divmod(a, b)\n" +
            "print(q)\n" +
            "print(r)\n");

        var q = Main(ir).OfType<Copy>().Single(c => c.Dst is Variable { Name: "main.q" });
        var r = Main(ir).OfType<Copy>().Single(c => c.Dst is Variable { Name: "main.r" });
        Assert.Equal(DataType.INT16, ((Variable)q.Dst).Type);
        Assert.Equal(DataType.INT16, ((Variable)r.Dst).Type);
    }

    [Fact]
    public void DivmodOfTwoInt8_StoresAQuotientThatHoldsMinusMinOverMinusOne()
    {
        // divmod(-128, -1) == (128, 0): the quotient does not fit an int8. The same
        // rank-by-size rule that truncated uint8/int8 left this one an int8 too.
        var ir = Gen(
            "a: int8 = int8(GPIOR0.value) - 128\n" +
            "b: int8 = int8(GPIOR0.value) - 1\n" +
            "q, r = divmod(a, b)\n" +
            "print(q)\n");

        var q = Main(ir).OfType<Copy>().Single(c => c.Dst is Variable { Name: "main.q" });
        Assert.Equal(DataType.INT16, ((Variable)q.Dst).Type);
    }

    [Fact]
    public void DivmodOfInt32ByInt8_RefusesWhenTheQuotientCanExceedInt32()
    {
        // divmod(INT32_MIN, -1) == (2147483648, 0): the rank formula asks for a width
        // above int32 that PyMCU does not have, and the clamp stored it back into
        // int32 where -a wraps to -2147483648. With no wider integer type the pair
        // is refused when the operands' ranges can actually reach the overflow --
        // the same ValRange criterion the arithmetic operators promote on.
        var ex = Assert.ThrowsAny<Exception>(() => Gen(
            "a: int32 = int32(GPIOR0.value) - 2147483647 - 1\n" +
            "b: int8 = int8(GPIOR0.value) - 1\n" +
            "q, r = divmod(a, b)\n"));
        Assert.Contains("can exceed int32", ex.Message);
    }

    [Fact]
    public void DivmodOfInt32ByAPositiveDivisorRangeStillCompiles()
    {
        // Only a divisor that can be -1 threatens the quotient: the temporary's
        // recorded range (0..255) proves b is never -1, so every quotient the pair
        // produces stays inside int32 and the result keeps the widest type.
        var ir = Gen(
            "a: int32 = int32(GPIOR0.value) - 2147483647 - 1\n" +
            "q, r = divmod(a, int8(GPIOR0.value) + 128)\n" +
            "print(q)\n");

        var q = Main(ir).OfType<Copy>().Single(c => c.Dst is Variable { Name: "main.q" });
        Assert.Equal(DataType.INT32, ((Variable)q.Dst).Type);
    }

    [Fact]
    public void DivmodOfInt32MinLiteralByInt8_RefusesWhenTheQuotientCanExceedInt32()
    {
        // Same refusal as above, but the dividend is the LITERAL -2147483648: every
        // negative constant was sized INT16 whatever its value, so the rank formula
        // never asked for a width above int32 and the ValRange refusal was skipped --
        // the pair compiled to int32 and the quotient 2147483648 printed as
        // -2147483648. The literal must enter with its own range.
        var ex = Assert.ThrowsAny<Exception>(() => Gen(
            "b: int8 = int8(GPIOR0.value) - 1\n" +
            "q, r = divmod(-2147483648, b)\n"));
        Assert.Contains("can exceed int32", ex.Message);
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

    // P2 AVR gaps bundle, item 3: `v = divmod(a, b)` (bound to one name) and
    // `print(divmod(a, b))` used to be refused outright -- PyMCU had no general runtime
    // tuple VALUE and only supported the two-target unpack. Both now read the same
    // multi-return-call sentinel `q, r = divmod(a, b)` and `f()` (a regular tuple-returning
    // call) already answer through (pendingTupleCount == -1, lastTupleResults), so they
    // bind/print the tuple the same way any other tuple-returning call does.
    [Fact]
    public void BindingADivmodResultToOneName_IsNowATuple()
    {
        // (quotient, remainder) prints as a 2-tuple: one decimal write per element (q=3,
        // r=2), not a single write of some collapsed scalar.
        var ir = Gen("v = divmod(17, 5)\nprint(v)\n");
        var calls = Main(ir).OfType<Call>().ToList();
        Assert.Equal(2, calls.Count(c => c.FunctionName.Contains("uart_write_decimal")));
    }

    [Fact]
    public void PrintingADivmodResultDirectly_PrintsTheTuple()
    {
        var ir = Gen("print(divmod(17, 5))\n");
        var calls = Main(ir).OfType<Call>().ToList();
        Assert.Equal(2, calls.Count(c => c.FunctionName.Contains("uart_write_decimal")));
    }

    // A position that is not an unpack, a single-name binding, or a print() argument --
    // there is still no general runtime tuple VALUE to pass around.
    [Fact]
    public void ADivmodResultPassedAsAnArgument_IsStillRefused()
    {
        string msg = Assert.ThrowsAny<Exception>(() => Gen(
            "def show(t):\n    print(t)\nshow(divmod(17, 5))\n")).Message;
        Assert.Contains("divmod() returns a 2-tuple", msg);
        Assert.Contains("q, r = divmod(a, b)", msg);
    }
}
