using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;

namespace PyMCU.IntegrationTests.Tests;

/// <summary>
/// Integration tests for examples/avr/checksum.
/// Receives bytes over UART; after every 4 bytes outputs the XOR checksum
/// (one byte) followed by '\n'.
/// Tests: AugAssign XOR, uart.read(), byte counter, conditional reset.
/// </summary>
[TestFixture]
public class ChecksumTests
{
    private string _hex = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _hex = PymcuCompiler.Build("checksum");

    [Test]
    public void Boot_SendsBanner()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "CHECKSUM");
        uno.Serial.Should().ContainLine("CHECKSUM");
    }

    [Test]
    public void FourBytes_OutputsXorChecksum()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "CHECKSUM\n");
        var before = uno.Serial.ByteCount;

        // 0xAA ^ 0x55 ^ 0xF0 ^ 0x0F = 0x00
        uno.Serial.InjectByte(0xAA);
        uno.RunMilliseconds(10);
        uno.Serial.InjectByte(0x55);
        uno.RunMilliseconds(10);
        uno.Serial.InjectByte(0xF0);
        uno.RunMilliseconds(10);
        uno.Serial.InjectByte(0x0F);
        uno.RunUntilSerialBytes(uno.Serial, before + 2, maxMs: 200); // checksum byte + '\n'

        uno.Serial.Bytes[before].Should().Be(0x00, "XOR of AA,55,F0,0F = 0x00");
        uno.Serial.Bytes[before + 1].Should().Be((byte)'\n');
    }

    [Test]
    public void FourBytes_NonZeroChecksum()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "CHECKSUM\n");
        var before = uno.Serial.ByteCount;

        // 0x01 ^ 0x02 ^ 0x04 ^ 0x08 = 0x0F
        uno.Serial.InjectByte(0x01);
        uno.RunMilliseconds(10);
        uno.Serial.InjectByte(0x02);
        uno.RunMilliseconds(10);
        uno.Serial.InjectByte(0x04);
        uno.RunMilliseconds(10);
        uno.Serial.InjectByte(0x08);
        uno.RunUntilSerialBytes(uno.Serial, before + 2, maxMs: 200);

        uno.Serial.Bytes[before].Should().Be(0x0F, "XOR of 01,02,04,08 = 0x0F");
    }

    [Test]
    public void TwoGroups_OutputsTwoChecksums()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "CHECKSUM\n");
        var before = uno.Serial.ByteCount;

        // Group 1: 0xFF ^ 0xFF ^ 0xFF ^ 0xFF = 0x00
        foreach (var b in new byte[] { 0xFF, 0xFF, 0xFF, 0xFF })
        {
            uno.Serial.InjectByte(b);
            uno.RunMilliseconds(10);
        }
        uno.RunUntilSerialBytes(uno.Serial, before + 2, maxMs: 200);

        // Group 2: 0x12 ^ 0x34 ^ 0x56 ^ 0x78 = 0x12^0x34^0x56^0x78
        byte expected2 = (byte)(0x12 ^ 0x34 ^ 0x56 ^ 0x78);
        foreach (var b in new byte[] { 0x12, 0x34, 0x56, 0x78 })
        {
            uno.Serial.InjectByte(b);
            uno.RunMilliseconds(10);
        }
        uno.RunUntilSerialBytes(uno.Serial, before + 4, maxMs: 200);

        uno.Serial.Bytes[before].Should().Be(0x00, "first group XOR = 0x00");
        uno.Serial.Bytes[before + 2].Should().Be(expected2, "second group XOR correct");
    }

    [Test]
    public void CounterResetsAfterGroup()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "CHECKSUM\n");
        var before = uno.Serial.ByteCount;

        // Send 8 bytes = 2 complete groups; verify both checksums
        byte[] grp1 = { 0xAA, 0x55, 0xF0, 0x0F }; // XOR = 0x00
        byte[] grp2 = { 0x01, 0x03, 0x07, 0x0F }; // XOR = 0x01^0x03^0x07^0x0F = 0x0A

        foreach (var b in grp1) { uno.Serial.InjectByte(b); uno.RunMilliseconds(10); }
        uno.RunUntilSerialBytes(uno.Serial, before + 2, maxMs: 200);

        foreach (var b in grp2) { uno.Serial.InjectByte(b); uno.RunMilliseconds(10); }
        uno.RunUntilSerialBytes(uno.Serial, before + 4, maxMs: 200);

        uno.Serial.Bytes[before].Should().Be(0x00, "group 1 XOR = 0x00");
        byte expectedXor2 = (byte)(0x01 ^ 0x03 ^ 0x07 ^ 0x0F);
        uno.Serial.Bytes[before + 2].Should().Be(expectedXor2, "group 2 XOR correct");
    }

    private ArduinoUnoSimulation Sim()
    {
        var uno = new ArduinoUnoSimulation();
        uno.WithHex(_hex);
        return uno;
    }
}
