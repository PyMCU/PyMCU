using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// PyMCU#463. <c>pow(x, 2.5)</c> with a runtime float base is how CircuitPython applies
/// sRGB gamma (adafruit_tcs34725). The builtin used to demand two compile-time integer
/// constants, so the line never built.
///
/// A non-negative integer exponent still unrolls to multiply. A fractional exponent, or a
/// negative one on a float base, is a CALL to __pymcu_powf (RuntimeHelpers.cs) -- the
/// domain-checking software-float routine EmitPowBuiltin's runtime-float case already used
/// for the bare pow() builtin. `**` used to emit a raw IR <c>BinaryOp.Pow</c> instead, which
/// the AVR backend lowers to a bare `CALL powf` (avr-libc's libm powf, no domain checks at
/// all): `0.0 ** -1` and `(-8.0) ** 0.5` silently answered whatever IEEE-754 powf gave
/// instead of raising ValueError like pow() does for the exact same values. Both spellings
/// now go through __pymcu_powf, so `BinaryOp.Pow` is no longer emitted by either.
/// </summary>
public class FloatPowTests
{
    private static ProgramIR Gen(string body) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(Prelude + body).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });

    private const string Prelude =
        "from pymcu.chips.atmega328p import GPIOR0\n" +
        "from pymcu.types import uint8\n" +
        "\n" +
        "def main() -> None:\n";

    private static bool EmitsPow(ProgramIR ir) =>
        ir.Functions.SelectMany(f => f.Body).OfType<Binary>().Any(b => b.Op == PyMCU.IR.BinaryOp.Pow)
        || ir.Functions.SelectMany(f => f.Body).OfType<Call>()
            .Any(c => c.FunctionName.Contains("powf", StringComparison.Ordinal));

    private static bool CallsPymcuPowf(ProgramIR ir) =>
        ir.Functions.SelectMany(f => f.Body).OfType<Call>()
            .Any(c => c.FunctionName == "__pymcu_powf");

    private static bool EmitsRawBinaryPow(ProgramIR ir) =>
        ir.Functions.SelectMany(f => f.Body).OfType<Binary>().Any(b => b.Op == PyMCU.IR.BinaryOp.Pow);

    [Fact]
    public void IntegerPowStillFolds()
    {
        var ir = Gen("    v: uint8 = pow(3, 4)\n");
        Assert.False(EmitsPow(ir));
        Assert.Contains(ir.Functions.SelectMany(f => f.Body).OfType<Copy>(),
            c => c.Src is Constant { Value: 81 });
    }

    [Fact]
    public void ARuntimeFloatBaseWithAFractionalExponent_EmitsPow()
    {
        var ir = Gen(
            "    a: uint8 = GPIOR0.value\n" +
            "    x: float = float(a) / 256.0\n" +
            "    y: float = pow(x, 2.5)\n");
        Assert.True(EmitsPow(ir));
    }

    [Fact]
    public void ThePowerOperator_WithAFractionalExponent_EmitsTheSameOp()
    {
        var ir = Gen(
            "    a: uint8 = GPIOR0.value\n" +
            "    x: float = float(a) / 256.0\n" +
            "    y: float = x ** 2.5\n");
        Assert.True(EmitsPow(ir));
    }

    [Fact]
    public void AFloatBaseWithAnIntegerExponent_UnrollsAndDoesNotPullPow()
    {
        var ir = Gen(
            "    a: uint8 = GPIOR0.value\n" +
            "    x: float = float(a) / 256.0\n" +
            "    y: float = x ** 2\n");
        Assert.False(EmitsPow(ir));
        Assert.Contains(ir.Functions.SelectMany(f => f.Body).OfType<Binary>(),
            b => b.Op == PyMCU.IR.BinaryOp.Mul);
    }

    // ---- ** and pow() must reach the SAME domain-checked routine (float-edges campaign) ----

    [Fact]
    public void APowerOperatorWithANegativeIntegerExponentOnAFloatBase_CallsPymcuPowf()
    {
        // `x ** -1` used to be refused outright: TryIntExponent does not care whether the
        // exponent's -1 came from an int base (where Python widens to float, unimplemented
        // here) or a float one (where it is ordinary reciprocal exponentiation, 2.0 ** -1 ==
        // 0.5). The float-base case is not the one the refusal is for.
        var ir = Gen(
            "    a: uint8 = GPIOR0.value\n" +
            "    x: float = float(a) / 256.0 + 1.0\n" +
            "    y: float = x ** -1\n");
        Assert.True(CallsPymcuPowf(ir));
        Assert.False(EmitsRawBinaryPow(ir));
    }

    [Fact]
    public void APowerOperatorWithAFractionalExponent_CallsPymcuPowfNotRawBinaryPow()
    {
        // The regression this guards: `**`'s runtime-float fallback used to emit a raw
        // Binary(Pow), which the AVR backend lowers to avr-libc's libm `powf` -- no domain
        // checks at all, unlike __pymcu_powf (0.0 ** negative, negative base with a
        // non-integral exponent). Both must go through the one checked implementation.
        var ir = Gen(
            "    a: uint8 = GPIOR0.value\n" +
            "    x: float = float(a) / 256.0\n" +
            "    y: float = x ** 2.5\n");
        Assert.True(CallsPymcuPowf(ir));
        Assert.False(EmitsRawBinaryPow(ir));
    }

    [Fact]
    public void ZeroToAConstantNegativePower_IsRefusedAtCompileTime()
    {
        // Math.Pow(0, negative) is +Infinity in C#, which would otherwise silently fold to
        // that instead of raising -- disagreeing with the exact same expression evaluated at
        // RUNTIME through __pymcu_powf, whose `x == 0.0 and y < 0` check raises ValueError.
        string msg = Assert.ThrowsAny<Exception>(() => Gen("    y: float = 0.0 ** -1\n")).Message;
        Assert.Contains("cannot be raised to a negative power", msg);
    }

    [Fact]
    public void ANegativeConstantBaseWithANonIntegralExponent_IsRefusedAtCompileTime()
    {
        // Math.Pow(-8, 1/3) is NaN in C#; Python's (-8.0) ** (1/3) is a complex number, which
        // PyMCU has no type for. Mirrors __pymcu_powf's own `x < 0.0` runtime check so the
        // compile-time fold cannot silently disagree with the runtime routine.
        string msg = Assert.ThrowsAny<Exception>(
            () => Gen("    y: float = (-8.0) ** (1.0 / 3.0)\n")).Message;
        Assert.Contains("negative base needs an integral exponent", msg);
    }
}
