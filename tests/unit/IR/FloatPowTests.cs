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
/// A non-negative integer exponent still unrolls to multiply. A fractional exponent
/// lowers to IR <c>BinaryOp.Pow</c> (AVR <c>powf</c>).
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
}
