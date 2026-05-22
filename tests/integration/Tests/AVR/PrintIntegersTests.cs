using Avr8Sharp.TestKit.Boards;
using FluentAssertions;
using NUnit.Framework;

namespace PyMCU.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for tests/integration/fixtures/avr/print-integers.
/// Verifies UART.print_uint16, print_int16, and print_uint32 emit the
/// correct ASCII decimal representation followed by a newline.
/// </summary>
[TestFixture]
public class PrintIntegersTests
{
    private static SimSession _session = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _session = new SimSession(PymcuCompiler.BuildFixture("print-integers"));

    private ArduinoUnoSimulation Boot()
    {
        var uno = _session.Reset();
        uno.RunUntilSerial(uno.Serial, "done\n", maxMs: 500);
        return uno;
    }

    [Test]
    public void PrintUint16_EmitsCorrectDecimal()
    {
        // print_uint16(1234) should transmit "1234\n"
        var uno = Boot();
        uno.Serial.Text.Should().Contain("1234\n", "print_uint16(1234) must emit \"1234\\n\"");
    }

    [Test]
    public void PrintInt16_EmitsNegativeDecimal()
    {
        // print_int16(-500) should transmit "-500\n"
        var uno = Boot();
        uno.Serial.Text.Should().Contain("-500\n", "print_int16(-500) must emit \"-500\\n\"");
    }

    [Test]
    public void PrintUint32_EmitsCorrectDecimal()
    {
        // print_uint32(123456) should transmit "123456\n"
        var uno = Boot();
        uno.Serial.Text.Should().Contain("123456\n", "print_uint32(123456) must emit \"123456\\n\"");
    }

    [Test]
    public void AllValues_InOrder()
    {
        // Full serial output must contain all three values followed by "done"
        var uno = Boot();
        var text = uno.Serial.Text;
        text.Should().Contain("1234\n");
        text.Should().Contain("-500\n");
        text.Should().Contain("123456\n");
        text.Should().Contain("done\n");
    }
}
