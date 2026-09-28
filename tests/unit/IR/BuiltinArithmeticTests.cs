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
}
