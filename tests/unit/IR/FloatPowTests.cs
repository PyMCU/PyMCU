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

    // P2 AVR gaps bundle, item 4: CPython's ** and pow() raise ZeroDivisionError for
    // 0.0 ** negative (a genuine RUNTIME exception -- CPython does not refuse this at
    // compile time), not the ValueError math.pow() raises for the identical value. This
    // used to be a CompileError for a compile-time-constant operand pair, folding
    // Math.Pow(0, negative)'s +Infinity into a refusal instead of CPython's actual
    // exception; it now raises ZeroDivisionError at run time like `0 ** -1`/`pow(0, -1)`
    // already did for an int base.
    [Fact]
    public void ZeroToAConstantNegativePower_RaisesZeroDivisionErrorAtRunTime()
    {
        var ir = Gen("    y: float = 0.0 ** -1\n");
        Assert.Contains(ir.Functions.SelectMany(f => f.Body).OfType<SignalError>(),
            r => r.Code is Constant { Value: 6 });
    }

    [Fact]
    public void PowOfZeroBaseAndAConstantNegativeExponent_RaisesZeroDivisionErrorAtRunTime()
    {
        var ir = Gen("    y: float = pow(0.0, -1)\n");
        Assert.Contains(ir.Functions.SelectMany(f => f.Body).OfType<SignalError>(),
            r => r.Code is Constant { Value: 6 });
    }

    // math.pow(0.0, -1) is untouched: it is an ordinary stdlib call straight to
    // __pymcu_powf (its ENTIRE body is `return __pymcu_powf(x, y)`, the shape
    // lib/src/pymcu/math/__init__.py's real pow() has), which never reaches
    // LowerPow/EmitPowBuiltin's new guard, so it keeps CPython's OWN math.pow
    // ValueError for the identical value that ** and pow() now raise ZeroDivisionError
    // for. A minimal stand-in module, not the real lib/src/pymcu/math -- the point under
    // test is the DISPATCH (bare builtin vs. an ordinary call), not math's own body.
    private static readonly Dictionary<string, ProgramNode> MathStub = new()
    {
        ["math"] = new Parser(new Lexer(
            "def pow(x: float, y: float) -> float:\n"
            + "    return __pymcu_powf(x, y)\n").Tokenize()).ParseProgram(),
    };

    [Fact]
    public void MathPowOfZeroBaseAndANegativeExponent_StillCallsPymcuPowf_NoGuard()
    {
        var ir = new IRGenerator().Generate(
            new Parser(new Lexer(
                "import math\n" + Prelude
                + "    a: uint8 = GPIOR0.value\n"
                + "    y: float = math.pow(float(a), -1.0)\n").Tokenize()).ParseProgram(),
            MathStub, new DeviceConfig { Arch = "avr" });
        Assert.True(CallsPymcuPowf(ir));
        // Scoped to MAIN, not the whole program: __pymcu_powf's own body divides by a
        // couple of runtime loop counters (its log2/exp series), each with the ordinary
        // `/` operator's own div-by-zero guard -- also code 6, unrelated to this test,
        // and living in __pymcu_powf's OWN function body, not main's.
        Assert.DoesNotContain(
            ir.Functions.Single(f => f.Name == "main").Body.OfType<SignalError>(),
            r => r.Code is Constant { Value: 6 });
    }

    [Fact]
    public void PowOfARuntimeZeroBaseWithARuntimeNegativeExponent_RaisesZeroDivisionError()
    {
        // The guard must be a RUN-TIME check, not just a compile-time-constant special
        // case: a base/exponent the compiler cannot fold still has to raise for the
        // (0.0, negative) pair it happens to carry when the program runs.
        var ir = Gen(
            "    a: uint8 = GPIOR0.value\n" +
            "    y: float = pow(float(a) * 0.0, 0 - 1 - int16(a))\n");
        Assert.Contains(ir.Functions.SelectMany(f => f.Body).OfType<SignalError>(),
            r => r.Code is Constant { Value: 6 });
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
