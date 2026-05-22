using Avr8Sharp.TestKit.Boards;
using FluentAssertions;
using NUnit.Framework;

namespace PyMCU.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for the bitcast() builtin.
/// Verifies raw byte reinterpretation (no value conversion) for:
///   - bitcast(uint32, float): IEEE 754 bit extraction
///   - bitcast(float, uint32): float reconstruction from raw bits
///   - bitcast(int8, uint8):   signed reinterpretation of same-size integer
/// </summary>
[TestFixture]
public class BitcastTests
{
    private static SimSession _session = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _session = new SimSession(PymcuCompiler.BuildFixture("bitcast"));

    private ArduinoUnoSimulation Boot()
    {
        var uno = _session.Reset();
        uno.RunUntilSerial(uno.Serial, "BC\n", maxMs: 200);
        return uno;
    }

    [Test]
    public void Boot_SendsBanner() =>
        Boot().Serial.Text.Should().Contain("BC");

    [Test]
    public void BitcastFloatToUint32_Msb_IsCorrect()
    {
        // bitcast(uint32, 1.0f): IEEE 754 = 0x3F800000, MSB = 0x3F
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("F:3F\n"), maxMs: 300);
        uno.Serial.Text.Should().Contain("F:3F", "MSB of IEEE 754 bits of 1.0f should be 0x3F");
    }

    [Test]
    public void BitcastFloatToUint32_Byte2_IsCorrect()
    {
        // bitcast(uint32, 1.0f): IEEE 754 = 0x3F800000, byte2 = 0x80
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("G:80\n"), maxMs: 300);
        uno.Serial.Text.Should().Contain("G:80", "byte2 of IEEE 754 bits of 1.0f should be 0x80");
    }

    [Test]
    public void BitcastUint32ToFloat_RoundTrip_ReturnsOriginal()
    {
        // bitcast(float, 0x3F800000) == 1.0 -> result = 1
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("H:01\n"), maxMs: 300);
        uno.Serial.Text.Should().Contain("H:01", "bitcast(float, 0x3F800000) should equal 1.0");
    }

    [Test]
    public void BitcastUint8ToInt8_255_IsNegative()
    {
        // bitcast(int8, 255) reinterprets 0xFF as -1 -> negative -> result = 1
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("I:01\n"), maxMs: 300);
        uno.Serial.Text.Should().Contain("I:01", "bitcast(int8, 255) should be negative (-1)");
    }
}
