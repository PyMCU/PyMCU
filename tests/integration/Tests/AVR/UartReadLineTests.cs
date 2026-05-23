using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;

namespace PyMCU.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for fixtures/avr/uart-readline.
/// Verifies that UART.read_line(buf, max_len) correctly reads a line terminated
/// by '\\n', stores bytes in the bytearray, and echoes them back with a length prefix.
/// </summary>
[TestFixture]
public class UartReadLineTests
{
    private SimSession _session = null!;

    [OneTimeSetUp]
    public void BuildFirmware() =>
        _session = new SimSession(PymcuCompiler.BuildFixture("uart-readline"));

    [Test]
    public void Boot_SendsBanner()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "RL", maxMs: 200);
        uno.Serial.Should().Contain("RL");
    }

    [Test]
    public void ReadLine_EchoesSimpleWord()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "RL\n", maxMs: 200);
        var after = uno.Serial.ByteCount;

        // Inject "Hi\n" — run 2 ms between each byte (9600 baud ≈ 1.04 ms/char)
        uno.Serial.InjectByte((byte)'H');
        uno.RunMilliseconds(2);
        uno.Serial.InjectByte((byte)'i');
        uno.RunMilliseconds(2);
        uno.Serial.InjectByte((byte)'\n');

        // Firmware sends: length (2), then 'H', 'i'
        uno.RunUntilSerialBytes(uno.Serial, after + 3, maxMs: 500);
        var response = uno.Serial.Bytes.Skip(after).Take(3).ToArray();
        response[0].Should().Be(2, "length of 'Hi' is 2");
        response[1].Should().Be((byte)'H');
        response[2].Should().Be((byte)'i');
    }

    [Test]
    public void ReadLine_StripsCarriageReturn()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "RL\n", maxMs: 200);
        var after = uno.Serial.ByteCount;

        // Inject "AB\r\n" — run 2 ms between each byte (9600 baud ≈ 1.04 ms/char)
        uno.Serial.InjectByte((byte)'A');
        uno.RunMilliseconds(2);
        uno.Serial.InjectByte((byte)'B');
        uno.RunMilliseconds(2);
        uno.Serial.InjectByte((byte)'\r');
        uno.RunMilliseconds(2);
        uno.Serial.InjectByte((byte)'\n');

        uno.RunUntilSerialBytes(uno.Serial, after + 3, maxMs: 500);
        var response = uno.Serial.Bytes.Skip(after).Take(3).ToArray();
        response[0].Should().Be(2, "CR stripped: length of 'AB' is 2");
        response[1].Should().Be((byte)'A');
        response[2].Should().Be((byte)'B');
    }

    [Test]
    public void ReadLine_NullTerminatesBuffer()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "RL\n", maxMs: 200);
        var after = uno.Serial.ByteCount;

        // Inject "X\n" — run 2 ms between each byte (9600 baud ≈ 1.04 ms/char)
        uno.Serial.InjectByte((byte)'X');
        uno.RunMilliseconds(2);
        uno.Serial.InjectByte((byte)'\n');

        // len(1) + X(1) = 2 bytes
        uno.RunUntilSerialBytes(uno.Serial, after + 2, maxMs: 500);
        var response = uno.Serial.Bytes.Skip(after).Take(2).ToArray();
        response[0].Should().Be(1, "length of 'X' is 1");
        response[1].Should().Be((byte)'X');
    }

    private ArduinoUnoSimulation Sim() => _session.Reset();
}
