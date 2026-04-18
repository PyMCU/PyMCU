using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;

namespace PyMCU.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for fixtures/avr/neg32-correctness.
///
/// Verifies that 32-bit unary negation (int32 -x) uses the full
/// NEG / COM+SBCI carry chain across all four bytes instead of the
/// old broken codegen that only operated on R24 (byte 0).
///
/// The carry-chain sequence for 32-bit (avr-gcc compatible):
///   NEG  R24                 ; R24 = -byte0, C = (byte0 != 0)
///   COM  R25 ; SBCI R25,255  ; R25 = ~byte1 + 1 - C
///   COM  R22 ; SBCI R22,255  ; R22 = ~byte2 + 1 - C
///   COM  R23 ; SBCI R23,255  ; R23 = ~byte3 + 1 - C
///
/// The critical test case is neg(0x00010000) = 0xFFFF0000:
///   byte0 and byte1 of the input are 0 so C propagates from NEG all the
///   way through; without the carry chain R22/R23 are left unmodified.
///
/// Data-space addresses (ATmega328P):
///   GPIOR0 = 0x3E   GPIOR1 = 0x4A   GPIOR2 = 0x4B   OCR0A  = 0x47
///   OCR0B  = 0x48   OCR1AL = 0x88   OCR1AH = 0x89   OCR1BL = 0x8A
/// </summary>
[TestFixture]
public class Neg32CorrectnessTests
{
    private const int Gpior0 = 0x3E;
    private const int Gpior1 = 0x4A;
    private const int Gpior2 = 0x4B;
    private const int Ocr0A  = 0x47;
    private const int Ocr0B  = 0x48;
    private const int Ocr1AL = 0x88;
    private const int Ocr1AH = 0x89;
    private const int Ocr1BL = 0x8A;

    private SimSession _session = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _session = new SimSession(PymcuCompiler.BuildFixture("neg32-correctness"));

    private ArduinoUnoSimulation Boot()
    {
        var uno = _session.Reset();
        uno.RunToBreak();
        return uno;
    }

    // --- Case 1: neg(5) = -5 = 0xFFFFFFFB -----------------------------------------

    [Test]
    public void NegFive_Byte0_Is0xFB() =>
        Boot().Data[Gpior0].Should().Be(0xFB, "-(int32)5 byte0 must be 0xFB");

    [Test]
    public void NegFive_Byte1_Is0xFF() =>
        Boot().Data[Gpior1].Should().Be(0xFF, "-(int32)5 byte1 must be 0xFF");

    [Test]
    public void NegFive_Byte2_Is0xFF() =>
        Boot().Data[Gpior2].Should().Be(0xFF, "-(int32)5 byte2 must be 0xFF");

    [Test]
    public void NegFive_Byte3_Is0xFF() =>
        Boot().Data[Ocr0A].Should().Be(0xFF, "-(int32)5 byte3 must be 0xFF");

    // --- Case 2: neg(0x00010000) = 0xFFFF0000 (lo two bytes are 0; the bug case) ---

    [Test]
    public void NegSixtyFiveThousandFiveHundredThirtySix_Byte0_Is0x00() =>
        Boot().Data[Ocr0B].Should().Be(0x00,
            "-(int32)65536 byte0 must be 0x00");

    [Test]
    public void NegSixtyFiveThousandFiveHundredThirtySix_Byte1_Is0x00() =>
        Boot().Data[Ocr1AL].Should().Be(0x00,
            "-(int32)65536 byte1 must be 0x00");

    [Test]
    public void NegSixtyFiveThousandFiveHundredThirtySix_Byte2_Is0xFF() =>
        Boot().Data[Ocr1AH].Should().Be(0xFF,
            "-(int32)65536 byte2 must be 0xFF; " +
            "the old single-NEG codegen would leave this as 0x00");

    [Test]
    public void NegSixtyFiveThousandFiveHundredThirtySix_Byte3_Is0xFF() =>
        Boot().Data[Ocr1BL].Should().Be(0xFF,
            "-(int32)65536 byte3 must be 0xFF; " +
            "the old single-NEG codegen would leave this as 0x00");
}
