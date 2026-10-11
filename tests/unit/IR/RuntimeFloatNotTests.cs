using System.Linq;
using FluentAssertions;
using PyMCU.Common.Models;
using PyMCU.Frontend;
using PyMCU.IR;
using PyMCU.IR.IRGenerator;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// `not x` on a RUN-TIME float is truth-by-zero: 0.0 and -0.0 are false, so `not` is true,
/// and every other float, NaN and +-inf included, is true, so `not` is false. The IR does
/// not lower it to a compare: it hands the backend a Unary Not whose source is a float and
/// whose destination is a one-byte bool, and the backend owns the zero test. That contract
/// is what ARM (fcmp oeq against 0.0) and AVR (sign-masked byte OR) both implement, so a
/// change that folded, widened or dropped this instruction would reach silicon wrong
/// without any single backend test noticing.
/// </summary>
public class RuntimeFloatNotTests
{
    private static ProgramIR Gen(string src) =>
        Optimizer.Optimize(new IRGenerator().Generate(
            new Parser(new Lexer(src).Tokenize()).ParseProgram(),
            new Dictionary<string, ProgramNode>(),
            new DeviceConfig { Arch = "avr" }));

    private const string Prelude =
        "from pymcu.chips.atmega328p import GPIOR0\n" +
        "from pymcu.types import uint8\n" +
        "\n" +
        "def main() -> None:\n";

    [Theory]
    [InlineData("    b: uint8 = GPIOR0.value\n    x: float = float(b)\n    GPIOR0.value = 1 if not x else 2\n")]
    [InlineData("    b: uint8 = GPIOR0.value\n    x: float = float(b) + 1e300\n    GPIOR0.value = 1 if not x else 2\n")]
    [InlineData("    b: uint8 = GPIOR0.value\n    x: float = float(b)\n    y: uint8 = 0\n    y = not x\n    GPIOR0.value = y\n")]
    public void NotOfARuntimeFloatReachesTheBackendAsAByteResultFromAFloatSource(string body)
    {
        var main = Gen(Prelude + body).Functions.Single(f => f.Name == "main").Body;

        var nots = main.OfType<Unary>().Where(u => u.Op == PyMCU.IR.UnaryOp.Not).ToList();

        nots.Should().ContainSingle(because: "a run-time float has no value to fold, so the test stays");
        nots[0].Src.Should().Match<Val>(v => v is Variable || v is Temporary);
        DataType dstType = nots[0].Dst switch
        {
            Temporary t => t.Type,
            Variable v => v.Type,
            _ => DataType.UNKNOWN,
        };
        dstType.Should().Be(DataType.UINT8, because: "a truth value is one byte, whatever the operand's width");
    }
}
