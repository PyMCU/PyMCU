using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;

namespace PyMCU.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for fixtures/avr/compat-mp-uart-readline.
/// Verifies machine.UART.readline() and machine.UART.readinto() from the
/// pymcu-micropython compat layer.
///
/// readline(buf, max_len) deviation from MicroPython:
///   - MicroPython: readline() -> bytes object (heap allocated)
///   - PyMCU:       readline(buf, max_len) -> uint8 count; caller owns buffer
///
/// readinto(buf, nbytes) deviation:
///   - MicroPython: readinto(buf) fills up to len(buf) bytes
///   - PyMCU:       readinto(buf, nbytes) reads exactly nbytes (explicit count)
/// </summary>
[TestFixture]
public class CompatMpUartReadlineTests
{
    private SimSession _session = null!;

    [OneTimeSetUp]
    public void BuildFirmware() =>
        _session = new SimSession(PymcuCompiler.BuildFixture("compat-mp-uart-readline"));

    [Test]
    public void Boot_SendsReadyBanner()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "READY", maxMs: 300);
        uno.Serial.Should().Contain("READY");
    }

    [Test]
    public void Readline_EchoesSimpleWord()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "READY\n", maxMs: 300);
        var after = uno.Serial.ByteCount;

        // Send 'L' command then "Hi\n"
        uno.Serial.InjectByte((byte)'L');
        uno.RunMilliseconds(2);
        uno.Serial.InjectByte((byte)'H');
        uno.RunMilliseconds(2);
        uno.Serial.InjectByte((byte)'i');
        uno.RunMilliseconds(2);
        uno.Serial.InjectByte((byte)'\n');

        // Firmware responds: count=2, 'H', 'i'
        uno.RunUntilSerialBytes(uno.Serial, after + 3, maxMs: 500);
        var resp = uno.Serial.Bytes.Skip(after).Take(3).ToArray();
        resp[0].Should().Be(2, "length of 'Hi' is 2");
        resp[1].Should().Be((byte)'H');
        resp[2].Should().Be((byte)'i');
    }

    [Test]
    public void Readline_StripsCarriageReturn()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "READY\n", maxMs: 300);
        var after = uno.Serial.ByteCount;

        // Send 'L' command then "OK\r\n"
        uno.Serial.InjectByte((byte)'L');
        uno.RunMilliseconds(2);
        uno.Serial.InjectByte((byte)'O');
        uno.RunMilliseconds(2);
        uno.Serial.InjectByte((byte)'K');
        uno.RunMilliseconds(2);
        uno.Serial.InjectByte((byte)'\r');
        uno.RunMilliseconds(2);
        uno.Serial.InjectByte((byte)'\n');

        // Firmware responds: count=2 (CR stripped), 'O', 'K'
        uno.RunUntilSerialBytes(uno.Serial, after + 3, maxMs: 500);
        var resp = uno.Serial.Bytes.Skip(after).Take(3).ToArray();
        resp[0].Should().Be(2, "CR stripped: length of 'OK' is 2");
        resp[1].Should().Be((byte)'O');
        resp[2].Should().Be((byte)'K');
    }

    [Test]
    public void Readline_SingleChar()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "READY\n", maxMs: 300);
        var after = uno.Serial.ByteCount;

        // Send 'L' command then "Z\n"
        uno.Serial.InjectByte((byte)'L');
        uno.RunMilliseconds(2);
        uno.Serial.InjectByte((byte)'Z');
        uno.RunMilliseconds(2);
        uno.Serial.InjectByte((byte)'\n');

        // Firmware responds: count=1, 'Z'
        uno.RunUntilSerialBytes(uno.Serial, after + 2, maxMs: 500);
        var resp = uno.Serial.Bytes.Skip(after).Take(2).ToArray();
        resp[0].Should().Be(1, "length of 'Z' is 1");
        resp[1].Should().Be((byte)'Z');
    }

    [Test]
    public void Readinto_ReadsExactlyThreeBytes()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "READY\n", maxMs: 300);
        var after = uno.Serial.ByteCount;

        // Send 'I' command then 3 bytes: 0x41 'A', 0x42 'B', 0x43 'C'
        uno.Serial.InjectByte((byte)'I');
        uno.RunMilliseconds(2);
        uno.Serial.InjectByte(0x41);
        uno.RunMilliseconds(2);
        uno.Serial.InjectByte(0x42);
        uno.RunMilliseconds(2);
        uno.Serial.InjectByte(0x43);

        // Firmware echoes all 3 bytes in order
        uno.RunUntilSerialBytes(uno.Serial, after + 3, maxMs: 500);
        var resp = uno.Serial.Bytes.Skip(after).Take(3).ToArray();
        resp[0].Should().Be(0x41, "first byte echoed");
        resp[1].Should().Be(0x42, "second byte echoed");
        resp[2].Should().Be(0x43, "third byte echoed");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private ArduinoUnoSimulation Sim() => _session.Reset();
}
