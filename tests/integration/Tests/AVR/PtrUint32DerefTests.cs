using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;

namespace PyMCU.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for fixtures/avr/ptr-uint32-deref.
///
/// Verifies that:
///   1. ptr[uint32] stores the ADDRESS as 16 bits (not 32 bits).
///   2. Dereferencing a ptr[uint32] reads all 4 bytes correctly.
///
/// Strategy: write 0x01, 0x02, 0x03, 0x04 to SRAM 0x0200-0x0203 via
/// ptr[uint8], then read them back as a single uint32 via ptr[uint32].
/// Little-endian: byte0=0x01 at 0x0200, byte3=0x04 at 0x0203.
///
/// Expected outputs:
///   GPIOR0=0x01, GPIOR1=0x02, GPIOR2=0x03, OCR0A=0x04
///
/// Data-space addresses (ATmega328P):
///   GPIOR0=0x3E, GPIOR1=0x4A, GPIOR2=0x4B, OCR0A=0x47
/// </summary>
[TestFixture]
public class PtrUint32DerefTests
{
    private const int Gpior0 = 0x3E;
    private const int Gpior1 = 0x4A;
    private const int Gpior2 = 0x4B;
    private const int Ocr0A  = 0x47;

    private string _hex = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _hex = PymcuCompiler.BuildFixture("ptr-uint32-deref");

    private ArduinoUnoSimulation Boot()
    {
        var uno = new ArduinoUnoSimulation();
        uno.WithHex(_hex);
        uno.RunToBreak();
        return uno;
    }

    [Test]
    public void Deref_Byte0_Is0x01() =>
        Boot().Data[Gpior0].Should().Be(0x01, "ptr[uint32] byte0 at 0x0200 must be 0x01");

    [Test]
    public void Deref_Byte1_Is0x02() =>
        Boot().Data[Gpior1].Should().Be(0x02, "ptr[uint32] byte1 at 0x0201 must be 0x02");

    [Test]
    public void Deref_Byte2_Is0x03() =>
        Boot().Data[Gpior2].Should().Be(0x03, "ptr[uint32] byte2 at 0x0202 must be 0x03");

    [Test]
    public void Deref_Byte3_Is0x04() =>
        Boot().Data[Ocr0A].Should().Be(0x04, "ptr[uint32] byte3 at 0x0203 must be 0x04");
}

