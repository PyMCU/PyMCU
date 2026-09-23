using FluentAssertions;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// `print(x < 5)` / `print(x is not None)` went to the decimal writer and sent
/// 1/0, where CPython and CircuitPython spell True/False. IsBoolExpr recognised
/// only literal and name bools; the comparison node itself is definitionally
/// 0/1, so print and f-string interpolation now stream it as a bool word pair.
/// </summary>
public class ComparisonPrintsBoolTests
{
    private const string Prelude =
        "from pymcu.types import uint8\n" +
        "def uart_write_str(s: const[str]):\n" +
        "    pass\n" +
        "def uart_write_decimal_u8(v: uint8):\n" +
        "    pass\n";

    private static ProgramIR Gen(string src) =>
        new IRGenerator().Generate(
            new Parser(new Lexer(Prelude + src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(), new DeviceConfig { Arch = "avr" });

    private static bool Calls(Instruction i, string name) =>
        i is Call c && c.FunctionName == name;

    [Fact]
    public void AComparisonArgument_PrintsAsWordsNotDecimal()
    {
        var main = Gen(
            "def main():\n" +
            "    x = uint8(3)\n" +
            "    print(x < 5)\n").Functions.Single(f => f.Name == "main");

        main.Body.Any(i => Calls(i, "uart_write_decimal_u8")).Should().BeFalse(
            "the comparison must not reach the decimal writer");
        main.Body.Any(i => Calls(i, "uart_write_str")).Should().BeTrue(
            "EmitStreamBool writes the True/False flash words");
        main.Body.Any(i => i is JumpIfNotZero || i is JumpIfZero
            || i is JumpIfNotEqual || i is JumpIfEqual).Should().BeTrue(
            "the bool stream branches on the comparison result");
    }

    [Fact]
    public void AnIsNoneArgument_PrintsAsWordsNotDecimal()
    {
        var main = Gen(
            "def main():\n" +
            "    x = uint8(3)\n" +
            "    print(x is not None)\n").Functions.Single(f => f.Name == "main");

        main.Body.Any(i => Calls(i, "uart_write_decimal_u8")).Should().BeFalse(
            "`is not None` is a bool, not a number");
    }
}
