using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;

namespace PyMCU.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for fixtures/avr/neg16-correctness.
///
/// Verifies that 16-bit unary negation (int16 -x) uses the correct
/// NEG / COM / SBCI R25, 255 sequence instead of the wrong
/// NEG / COM / ADC R25, R1 that was emitted by the original codegen.
///
/// Root cause of the bug:
///   After  NEG R24,  carry C = (original_lo != 0).
///   The correct high-byte fixup is SBCI R25, 255  which computes ~hi + 1 - C.
///   The wrong fixup was     ADC  R25, R1  which computes ~hi + 0 + C.
///   The two expressions are off by exactly 1 and in opposite directions of C,
///   so the result is always wrong unless lo happens to be zero AND carry is zero,
///   which never produces a visible error only coincidentally.
///
/// The hardest case to detect is when lo == 0:
///   -0x0100 = -256 = 0xFF00
///   Bug gives  0xFE00 (high byte 0xFE instead of 0xFF).
///
/// Data-space addresses (ATmega328P):
///   GPIOR0 = 0x3E   GPIOR1 = 0x4A   GPIOR2 = 0x4B
///   OCR0A  = 0x47   OCR0B  = 0x48   OCR1AL = 0x88
/// </summary>
[TestFixture]
public class Neg16CorrectnessTests
{
    private const int Gpior0 = 0x3E;
    private const int Gpior1 = 0x4A;
    private const int Gpior2 = 0x4B;
    private const int Ocr0A  = 0x47;
    private const int Ocr0B  = 0x48;
    private const int Ocr1AL = 0x88;

    private string _hex = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _hex = PymcuCompiler.BuildFixture("neg16-correctness");

    private ArduinoUnoSimulation Boot()
    {
        var uno = new ArduinoUnoSimulation();
        uno.WithHex(_hex);
        uno.RunToBreak();
        return uno;
    }

    // --- Case 1: neg(5) = -5 = 0xFFFB ------------------------------------------

    [Test]
    public void NegFive_LowByte_Is0xFB() =>
        Boot().Data[Gpior0].Should().Be(0xFB,
            "-(int16)5 low byte must be 0xFB");

    [Test]
    public void NegFive_HighByte_Is0xFF() =>
        Boot().Data[Gpior1].Should().Be(0xFF,
            "-(int16)5 high byte must be 0xFF");

    // --- Case 2: neg(256) = -256 = 0xFF00 (lo == 0; the critical bug case) ------

    [Test]
    public void NegTwoFiftySix_LowByte_Is0x00() =>
        Boot().Data[Gpior2].Should().Be(0x00,
            "-(int16)256 low byte must be 0x00");

    [Test]
    public void NegTwoFiftySix_HighByte_Is0xFF() =>
        Boot().Data[Ocr0A].Should().Be(0xFF,
            "-(int16)256 high byte must be 0xFF; " +
            "the old ADC R25,R1 codegen would give 0xFE here");

    [Test]
    public void NegTwoFiftySix_HighByte_IsNotWrongValue0xFE() =>
        Boot().Data[Ocr0A].Should().NotBe(0xFE,
            "0xFE is the value produced by the buggy ADC R25,R1 codegen");

    // --- Case 3: neg(-32768) = -32768 (wraps; 0x8000) ---------------------------

    [Test]
    public void NegMinInt16_LowByte_Is0x00() =>
        Boot().Data[Ocr0B].Should().Be(0x00,
            "-(int16)(-32768) wraps to -32768; low byte = 0x00");

    [Test]
    public void NegMinInt16_HighByte_Is0x80() =>
        Boot().Data[Ocr1AL].Should().Be(0x80,
            "-(int16)(-32768) wraps to -32768; high byte = 0x80");
}
