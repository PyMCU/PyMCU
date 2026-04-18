using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;

namespace PyMCU.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for examples/avr/nested-calls.
/// 3-level non-inline call chain: main → encode_byte(val, val) → nibble_to_hex(n).
/// For each val 0..255 (cycling), outputs: hi_char, lo_char, chk_byte, '\n'.
/// Tests: nested function calls, AVR calling convention (R24/R22), return values,
///        two calls to the same function within one function body.
/// </summary>
[TestFixture]
public class NestedCallsTests
{
    private SimSession _session = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _session = new SimSession(PymcuCompiler.BuildFixture("nested-calls"));

    [Test]
    public void Boot_SendsBanner()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "HEX ENCODE");
        uno.Serial.Should().ContainLine("HEX ENCODE");
    }

    [Test]
    public void Val0_OutputsZeroZeroNulNewline()
    {
        // val=0: hi=nibble_to_hex(0)='0'=0x30, lo='0'=0x30, chk=0x30^0x30=0x00, '\n'=0x0A
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "HEX ENCODE\n");
        var before = uno.Serial.ByteCount;
        uno.RunUntilSerialBytes(uno.Serial, before + 4, maxMs: 200);

        var line0 = uno.Serial.Bytes.Skip(before).Take(4).ToArray();
        line0[0].Should().Be((byte)'0', "hi nibble of 0x00 → '0'");
        line0[1].Should().Be((byte)'0', "lo nibble of 0x00 → '0'");
        line0[2].Should().Be(0x00,      "chk = '0'^'0' = 0x00");
        line0[3].Should().Be((byte)'\n');
    }

    [Test]
    public void Val1_OutputsZeroOneChecksum()
    {
        // val=1: hi='0'=0x30, lo='1'=0x31, chk=0x30^0x31=0x01
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "HEX ENCODE\n");
        var before = uno.Serial.ByteCount;
        uno.RunUntilSerialBytes(uno.Serial, before + 8, maxMs: 200);

        var line1 = uno.Serial.Bytes.Skip(before + 4).Take(4).ToArray();
        line1[0].Should().Be((byte)'0', "hi nibble of 0x01 → '0'");
        line1[1].Should().Be((byte)'1', "lo nibble of 0x01 → '1'");
        line1[2].Should().Be((byte)('0' ^ '1'), "chk = '0'^'1'");
    }

    [Test]
    public void ValF_OutputsZeroF()
    {
        // val=0x0F: hi='0'=0x30, lo='F'=0x46, chk=0x30^0x46=0x76
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "HEX ENCODE\n");
        var before = uno.Serial.ByteCount;
        // val=0x0F is the 16th value (index 15) → skip 15 lines (15*4 = 60 bytes)
        uno.RunUntilSerialBytes(uno.Serial, before + 16 * 4, maxMs: 500);

        var line15 = uno.Serial.Bytes.Skip(before + 15 * 4).Take(4).ToArray();
        line15[0].Should().Be((byte)'0', "hi nibble of 0x0F → '0'");
        line15[1].Should().Be((byte)'F', "lo nibble of 0x0F → 'F'");
        line15[2].Should().Be((byte)('0' ^ 'F'), "chk = '0'^'F'");
    }

    [Test]
    public void Val16_OutputsOneSix()
    {
        // val=0x10: hi='1'=0x31, lo='0'=0x30, chk=0x31^0x30=0x01
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "HEX ENCODE\n");
        var before = uno.Serial.ByteCount;
        uno.RunUntilSerialBytes(uno.Serial, before + 17 * 4, maxMs: 500);

        var line16 = uno.Serial.Bytes.Skip(before + 16 * 4).Take(4).ToArray();
        line16[0].Should().Be((byte)'1', "hi nibble of 0x10 → '1'");
        line16[1].Should().Be((byte)'0', "lo nibble of 0x10 → '0'");
        line16[2].Should().Be((byte)('1' ^ '0'), "chk = '1'^'0'");
    }

    [Test]
    public void First16Lines_AllCorrect()
    {
        // Verify the complete hex encoding for val 0x00..0x0F
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "HEX ENCODE\n");
        var before = uno.Serial.ByteCount;
        uno.RunUntilSerialBytes(uno.Serial, before + 16 * 4, maxMs: 500);

        var bytes = uno.Serial.Bytes.Skip(before).Take(16 * 4).ToArray();
        for (var val = 0; val < 16; val++)
        {
            // val = 0x00..0x0F: hi nibble is always 0, lo nibble is val
            var hiChar = (byte)'0';
            var loChar = val < 10 ? (byte)('0' + val) : (byte)('A' + val - 10);
            var chk    = (byte)(hiChar ^ loChar);

            var offset = val * 4;
            bytes[offset + 0].Should().Be(hiChar, $"val=0x{val:X2} hi char");
            bytes[offset + 1].Should().Be(loChar, $"val=0x{val:X2} lo char");
            bytes[offset + 2].Should().Be(chk,    $"val=0x{val:X2} checksum");
            bytes[offset + 3].Should().Be((byte)'\n', $"val=0x{val:X2} newline");
        }
    }

    private ArduinoUnoSimulation Sim() => _session.Reset();
}
