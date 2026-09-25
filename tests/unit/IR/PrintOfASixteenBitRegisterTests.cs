using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// Found while building the fixture for PyMCU#492. `print(TCNT1.value)` on a `ptr[uint16]`
/// register name formatted the LOW BYTE alone: 0x1234 printed as 52, with nothing said.
///
/// A register read is a MemoryAddress carrying the width its `ptr[T]` declared, and print's
/// argument-width pick had arms for a Variable, a Temporary and a Constant and a UINT8
/// default for everything else. Assigning the same read to a `uint16` local first printed it
/// whole, which is what made the truncation look like the register's own fault rather than
/// print's.
/// </summary>
public class PrintOfASixteenBitRegisterTests
{
    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" });

    private static List<string> DecimalWriters(ProgramIR ir) =>
        ir.Functions.SelectMany(f => f.Body).OfType<Call>()
            .Select(c => c.FunctionName)
            .Where(n => n.Contains("write_decimal"))
            .ToList();

    private const string Header =
        "from pymcu.types import ptr, uint8, uint16, const\n" +
        "\n" +
        "GPIOR0: ptr[uint8] = ptr(0x3E)\n" +
        "TCNT1: ptr[uint16] = ptr(0x84)\n" +
        "\n" +
        "def uart_write_str(s: const[str]):\n" +
        "    pass\n" +
        "\n" +
        "def uart_write_decimal_u8(v: uint8):\n" +
        "    pass\n" +
        "\n" +
        "def uart_write_decimal_u16(v: uint16):\n" +
        "    pass\n" +
        "\n";

    [Fact]
    public void ASixteenBitRegister_PrintsThroughTheSixteenBitWriter()
    {
        var ir = Gen(Header +
            "def main():\n" +
            "    print(TCNT1.value)\n");

        Assert.Contains(DecimalWriters(ir), n => n.EndsWith("uart_write_decimal_u16"));
    }

    [Fact]
    public void AnEightBitRegister_StillPrintsThroughTheEightBitWriter()
    {
        var ir = Gen(Header +
            "def main():\n" +
            "    print(GPIOR0.value)\n");

        Assert.Contains(DecimalWriters(ir), n => n.EndsWith("uart_write_decimal_u8"));
        Assert.DoesNotContain(DecimalWriters(ir), n => n.EndsWith("uart_write_decimal_u16"));
    }
}
