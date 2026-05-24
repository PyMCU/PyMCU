using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;

namespace PyMCU.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for the input() builtin (fixtures/avr/input-builtin).
///
/// Verifies that:
///   - input() emits its prompt string before blocking on UART read
///   - input() fills the bytearray buffer with the received line (stops at newline)
///   - The UART preamble is auto-injected (no explicit UART() in user source)
/// </summary>
[TestFixture]
public class InputTests
{
    private SimSession _session = null!;

    [OneTimeSetUp]
    public void BuildFirmware() =>
        _session = new SimSession(PymcuCompiler.BuildFixture("input-builtin"));

    [Test]
    public void Boot_SendsBanner()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "IN\n", maxMs: 200);
        uno.Serial.Text.Should().Contain("IN");
    }

    [Test]
    public void Input_EmitsPromptBeforeBlocking()
    {
        var uno = Sim();
        // After boot banner, input(">") immediately emits ">" and then blocks.
        uno.RunUntilSerial(uno.Serial, ">", maxMs: 200);
        uno.Serial.Text.Should().Contain(">");
    }

    [Test]
    public void Input_ReadsLineAndReturnsCorrectCount()
    {
        var uno = Sim();
        // Wait for prompt — firmware is now blocked in uart_read_line
        uno.RunUntilSerial(uno.Serial, ">", maxMs: 200);
        var afterPrompt = uno.Serial.ByteCount;

        // Inject "Hi\n" — 1 ms gap is well above the 8.7 µs bit time at 115200 baud
        uno.Serial.InjectByte((byte)'H');
        uno.RunMilliseconds(1);
        uno.Serial.InjectByte((byte)'i');
        uno.RunMilliseconds(1);
        uno.Serial.InjectByte((byte)'\n');

        // Firmware prints count "2\n" after reading 'H' and 'i'
        uno.RunUntilSerial(uno.Serial, "2\n", maxMs: 500);
        uno.Serial.Text.Should().Contain("2");
    }

    [Test]
    public void Input_StripsCarriageReturnFromCrLf()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, ">", maxMs: 200);

        // Inject "AB\r\n" — CR must be silently discarded
        uno.Serial.InjectByte((byte)'A');
        uno.RunMilliseconds(1);
        uno.Serial.InjectByte((byte)'B');
        uno.RunMilliseconds(1);
        uno.Serial.InjectByte((byte)'\r');
        uno.RunMilliseconds(1);
        uno.Serial.InjectByte((byte)'\n');

        // Count should still be 2 (CR not counted)
        uno.RunUntilSerial(uno.Serial, "2\n", maxMs: 500);
        uno.Serial.Text.Should().Contain("2");
    }

    [Test]
    public void Input_EmptyLineReturnsZeroCount()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, ">", maxMs: 200);

        // Inject just "\n"
        uno.Serial.InjectByte((byte)'\n');

        // Count should be 0
        uno.RunUntilSerial(uno.Serial, "0\n", maxMs: 500);
        uno.Serial.Text.Should().Contain("0");
    }

    private ArduinoUnoSimulation Sim() => _session.Reset();
}
