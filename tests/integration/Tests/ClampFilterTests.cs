using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;

namespace PyMCU.IntegrationTests.Tests;

/// <summary>
/// Integration tests for examples/avr/clamp-filter.
/// clamp(val, lo, hi) tests the 3-argument calling convention (R24, R22, R20).
/// predict(prev, curr) calls clamp internally — a 2→3-arg nested call chain.
/// Tests: 3-arg function, multiple early return paths, uint8 >> 1 for avg,
///        chained function calls, state (prev) persisting across loop iterations.
/// </summary>
[TestFixture]
public class ClampFilterTests
{
    private string _hex = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _hex = PymcuCompiler.Build("clamp-filter");

    [Test]
    public void Boot_SendsBanner()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "CLAMP FILTER");
        uno.Serial.Should().ContainLine("CLAMP FILTER");
    }

    [Test]
    public void InRange_BytePassedThrough()
    {
        // Send 'A' (0x41 = 65) — within [32,126] range, should echo back unchanged
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "CLAMP FILTER\n");
        var before = uno.Serial.ByteCount;

        uno.Serial.InjectByte(0x41); // 'A'
        uno.RunUntilSerialBytes(uno.Serial, before + 3, maxMs: 200); // clamped + predicted + '\n'

        uno.Serial.Bytes[before].Should().Be(0x41, "clamped('A', 32, 126) = 'A'");
        uno.Serial.Bytes[before + 2].Should().Be((byte)'\n');
    }

    [Test]
    public void BelowLo_ClampedTo32()
    {
        // Send 0x0A (10) — below 32, should be clamped to 32
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "CLAMP FILTER\n");
        var before = uno.Serial.ByteCount;

        uno.Serial.InjectByte(0x0A); // value 10, below lo=32
        uno.RunUntilSerialBytes(uno.Serial, before + 3, maxMs: 200);

        uno.Serial.Bytes[before].Should().Be(32, "clamp(10, 32, 126) = 32");
    }

    [Test]
    public void AboveHi_ClampedTo126()
    {
        // Send 0xFF (255) — above 126, should be clamped to 126
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "CLAMP FILTER\n");
        var before = uno.Serial.ByteCount;

        uno.Serial.InjectByte(0xFF);
        uno.RunUntilSerialBytes(uno.Serial, before + 3, maxMs: 200);

        uno.Serial.Bytes[before].Should().Be(126, "clamp(255, 32, 126) = 126");
    }

    [Test]
    public void PredictedValue_IsAverageOfPrevAndClamped()
    {
        // Initial prev = 64. Send 64 → clamped=64, predicted=(64+64)>>1=64
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "CLAMP FILTER\n");
        var before = uno.Serial.ByteCount;

        uno.Serial.InjectByte(64); // 0x40
        uno.RunUntilSerialBytes(uno.Serial, before + 3, maxMs: 200);

        // prev was 64, curr=64: avg=(64+64)>>1=64, clamp(64,32,126)=64
        uno.Serial.Bytes[before].Should().Be(64,  "clamped = 64");
        uno.Serial.Bytes[before + 1].Should().Be(64, "predicted = (64+64)>>1 = 64");
    }

    [Test]
    public void PredictedValue_AveragesWithPrevious()
    {
        // Send 0x00 (→ clamped=32), then 64 → predicted=(32+64)>>1=48
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "CLAMP FILTER\n");
        var before = uno.Serial.ByteCount;

        // Byte 1: 0 → clamped=32, predicted=(64+32)>>1=48; prev becomes 32
        uno.Serial.InjectByte(0x00);
        uno.RunUntilSerialBytes(uno.Serial, before + 3, maxMs: 200);
        uno.Serial.Bytes[before].Should().Be(32, "clamp(0,32,126)=32");
        uno.Serial.Bytes[before + 1].Should().Be(48, "predict(64,32)=(64+32)>>1=48");

        // Byte 2: 64 → clamped=64, predicted=(32+64)>>1=48; prev becomes 64
        var before2 = uno.Serial.ByteCount;
        uno.Serial.InjectByte(64);
        uno.RunUntilSerialBytes(uno.Serial, before2 + 3, maxMs: 200);
        uno.Serial.Bytes[before2].Should().Be(64, "clamped=64");
        uno.Serial.Bytes[before2 + 1].Should().Be(48, "predict(32,64)=(32+64)>>1=48");
    }

    [Test]
    public void AtBoundaryValues_Accepted()
    {
        // Send lo=32 and hi=126 exactly — both should pass through unchanged
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "CLAMP FILTER\n");
        var before = uno.Serial.ByteCount;

        uno.Serial.InjectByte(32);
        uno.RunUntilSerialBytes(uno.Serial, before + 3, maxMs: 200);
        uno.Serial.Bytes[before].Should().Be(32, "lo boundary not clamped");

        var before2 = uno.Serial.ByteCount;
        uno.Serial.InjectByte(126);
        uno.RunUntilSerialBytes(uno.Serial, before2 + 3, maxMs: 200);
        uno.Serial.Bytes[before2].Should().Be(126, "hi boundary not clamped");
    }

    private ArduinoUnoSimulation Sim()
    {
        var uno = new ArduinoUnoSimulation();
        uno.WithHex(_hex);
        return uno;
    }
}
