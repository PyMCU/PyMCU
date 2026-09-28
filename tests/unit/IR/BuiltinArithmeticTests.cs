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
}
