using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;

namespace PyMCU.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for fixtures/avr/uint32-roundtrip.
///
/// Verifies that uint32 variables are stored and loaded as 4 full bytes.
/// Before the fix, SizeOf(UINT32)=4 but is16=(4==2)=false, so only
/// 1 byte was ever stored or loaded.
///
/// Test value: 0x12345678
///   GPIOR0 = 0x78 (byte 0, LSB), GPIOR1 = 0x56 (byte 1),
///   GPIOR2 = 0x34 (byte 2),      OCR0A  = 0x12 (byte 3, MSB)
///
/// Data-space addresses (ATmega328P):
///   GPIOR0=0x3E, GPIOR1=0x4A, GPIOR2=0x4B, OCR0A=0x47
/// </summary>
[TestFixture]
public class Uint32RoundtripTests
{
    private const int Gpior0 = 0x3E;
    private const int Gpior1 = 0x4A;
    private const int Gpior2 = 0x4B;
    private const int Ocr0A  = 0x47;

    private string _hex = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _hex = PymcuCompiler.BuildFixture("uint32-roundtrip");

    private ArduinoUnoSimulation Boot()
    {
        var uno = new ArduinoUnoSimulation();
        uno.WithHex(_hex);
        uno.RunToBreak();
        return uno;
    }

    [Test]
    public void Byte0_Lsb_Is0x78() =>
        Boot().Data[Gpior0].Should().Be(0x78, "0x12345678 byte0 (LSB) = 0x78");

    [Test]
    public void Byte1_Is0x56() =>
        Boot().Data[Gpior1].Should().Be(0x56, "0x12345678 byte1 = 0x56");

    [Test]
    public void Byte2_Is0x34() =>
        Boot().Data[Gpior2].Should().Be(0x34, "0x12345678 byte2 = 0x34 (was 0 before fix)");

    [Test]
    public void Byte3_Msb_Is0x12() =>
        Boot().Data[Ocr0A].Should().Be(0x12, "0x12345678 byte3 (MSB) = 0x12 (was 0 before fix)");
}

