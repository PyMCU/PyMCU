using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;

namespace PyMCU.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for fixtures/avr/div16-correctness.
///
/// Verifies that uint16 division and modulo call __div16 / __mod16 instead
/// of the old __div8 / __mod8 which silently truncated the dividend to 8 bits.
///
/// __div16 ABI:
///   Input:  R25:R24 (Dividend), R19:R18 (Divisor)
///   Output: R25:R24 (Quotient), R27:R26 (Remainder)
///
/// __mod16 ABI:
///   Input:  R25:R24 (Dividend), R19:R18 (Divisor)
///   Output: R25:R24 (Remainder)
///
/// Test cases:
///   1000 / 10  = 100   (quotient fits in one byte but dividend does not)
///   1000 % 10  = 0
///   65000 / 256 = 253  (large 16-bit dividend)
///   65000 % 256 = 232
///
/// Data-space addresses (ATmega328P):
///   GPIOR0 = 0x3E   GPIOR1 = 0x4A   GPIOR2 = 0x4B
///   OCR0A  = 0x47   OCR0B  = 0x48   OCR1AL = 0x88
/// </summary>
[TestFixture]
public class Div16CorrectnessTests
{
    private const int Gpior0 = 0x3E;
    private const int Gpior1 = 0x4A;
    private const int Gpior2 = 0x4B;
    private const int Ocr0A  = 0x47;
    private const int Ocr0B  = 0x48;
    private const int Ocr1AL = 0x88;

    private string _hex = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _hex = PymcuCompiler.BuildFixture("div16-correctness");

    private ArduinoUnoSimulation Boot()
    {
        var uno = new ArduinoUnoSimulation();
        uno.WithHex(_hex);
        uno.RunToBreak();
        return uno;
    }

    // --- 1000 / 10 = 100 ----------------------------------------------------------

    [Test]
    public void Div1000By10_Quotient_LowByte_Is100() =>
        Boot().Data[Gpior0].Should().Be(100,
            "1000 / 10 quotient low byte must be 100");

    [Test]
    public void Div1000By10_Quotient_HighByte_Is0() =>
        Boot().Data[Gpior1].Should().Be(0,
            "1000 / 10 quotient high byte must be 0");

    // --- 1000 % 10 = 0 ------------------------------------------------------------

    [Test]
    public void Mod1000By10_Is0() =>
        Boot().Data[Gpior2].Should().Be(0,
            "1000 % 10 must be 0");

    // --- 65000 / 256 = 253 --------------------------------------------------------
    // This test specifically catches the __div8 truncation bug because 65000 > 255.

    [Test]
    public void Div65000By256_Quotient_LowByte_Is253() =>
        Boot().Data[Ocr0A].Should().Be(253,
            "65000 / 256 quotient low byte must be 253; " +
            "__div8 would give wrong result because 65000 > 255");

    [Test]
    public void Div65000By256_Quotient_HighByte_Is0() =>
        Boot().Data[Ocr0B].Should().Be(0,
            "65000 / 256 quotient high byte must be 0");

    // --- 65000 % 256 = 232 --------------------------------------------------------

    [Test]
    public void Mod65000By256_Is232() =>
        Boot().Data[Ocr1AL].Should().Be(232,
            "65000 % 256 remainder must be 232; " +
            "__mod8 would give wrong result because 65000 > 255");
}
