using Avr8Sharp.TestKit.Boards;
using FluentAssertions;
using NUnit.Framework;

namespace Whipsnake.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for examples/avr/new-builtins.
/// Exercises the new language features:
///   - zip(a, b)          compile-time paired iteration
///   - reversed([list])   compile-time reversed iteration
///   - str(n)             compile-time integer to decimal string
///   - pow(x, n)          compile-time integer power
///   - x ** n             compile-time power operator
/// </summary>
[TestFixture]
public class NewBuiltinsTests
{
    private static string _hex = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _hex = PymcuCompiler.Build("new-builtins");

    private ArduinoUnoSimulation Boot()
    {
        var uno = new ArduinoUnoSimulation();
        uno.WithHex(_hex);
        uno.RunUntilSerial(uno.Serial, "NB\n", maxMs: 200);
        return uno;
    }

    [Test]
    public void Boot_SendsBanner() =>
        Boot().Serial.Text.Should().Contain("NB");

    [Test]
    public void Zip_SumOfPairs_IsCorrect()
    {
        // zip([1,2,3],[10,20,30]): (1+10)+(2+20)+(3+30) = 11+22+33 = 66 = 0x42
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("Z:42\n"), maxMs: 300);
        uno.Serial.Text.Should().Contain("Z:42", "zip sum should be 66=0x42");
    }

    [Test]
    public void Reversed_SumInReverseOrder_IsCorrect()
    {
        // reversed([5,10,15,20]) iterated: 20+15+10+5 = 50 = 0x32
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("R:32\n"), maxMs: 300);
        uno.Serial.Text.Should().Contain("R:32", "reversed sum should be 50=0x32");
    }

    [Test]
    public void Str_CompileTimeConversion_IsCorrect()
    {
        // str(42) => "42" printed to UART
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("S:42\n"), maxMs: 300);
        uno.Serial.Text.Should().Contain("S:42", "str(42) should produce '42'");
    }

    [Test]
    public void Pow_CompileTimePower_IsCorrect()
    {
        // pow(3, 4) = 81 = 0x51
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("P:51\n"), maxMs: 300);
        uno.Serial.Text.Should().Contain("P:51", "pow(3,4) should be 81=0x51");
    }

    [Test]
    public void PowerOperator_CompileTimeFold_IsCorrect()
    {
        // 2 ** 6 = 64 = 0x40
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("W:40\n"), maxMs: 300);
        uno.Serial.Text.Should().Contain("W:40", "2**6 should be 64=0x40");
    }

    [Test]
    public void UartReadNb_NoDataPending_ReturnsZero()
    {
        // uart.read_nb() with no pending RX data => 0 => NR:00
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("NR:00\n"), maxMs: 400);
        uno.Serial.Text.Should().Contain("NR:00", "uart.read_nb() with no RX data should return 0");
    }
}
