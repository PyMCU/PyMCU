using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;

namespace PyMCU.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for examples/avr/enum-state.
/// Verifies compile-time constant folding for integer type casts:
///   uart.write(uint8(300)) → byte 44  (300 mod 256 = 0x2C)
///   uart.write(uint8(256)) → byte  0  (256 mod 256 = 0x00)
///   uart.write(uint16(42)) → byte 42  (uint16 truncated to uint8 by uart.write signature)
/// No boot banner — the firmware sends raw bytes immediately.
/// </summary>
[TestFixture]
public class EnumStateTests
{
    private SimSession _session = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _session = new SimSession(PymcuCompiler.Build("enum-state"));

    [Test]
    public void Uint8_300_FoldsTo44()
    {
        // uint8(300) = 300 & 0xFF = 44 (0x2C). Must be the first byte.
        var uno = Sim();
        uno.RunUntilSerialBytes(uno.Serial, 1, maxMs: 200);
        uno.Serial.Bytes[0].Should().Be(44, "uint8(300) should constant-fold to 300 mod 256 = 44");
    }

    [Test]
    public void Uint8_256_FoldsToZero()
    {
        // uint8(256) = 256 & 0xFF = 0. Must be the second byte.
        var uno = Sim();
        uno.RunUntilSerialBytes(uno.Serial, 2, maxMs: 200);
        uno.Serial.Bytes[1].Should().Be(0, "uint8(256) should constant-fold to 256 mod 256 = 0");
    }

    [Test]
    public void Uint16_42_SendsLowByte()
    {
        // uart.write() accepts uint8; uint16(42) is truncated to low byte 42.
        var uno = Sim();
        uno.RunUntilSerialBytes(uno.Serial, 3, maxMs: 200);
        uno.Serial.Bytes[2].Should().Be(42,
            "uint16(42) passed to uart.write(uint8) should send the low byte 42");
    }

    private ArduinoUnoSimulation Sim() => _session.Reset();
}
