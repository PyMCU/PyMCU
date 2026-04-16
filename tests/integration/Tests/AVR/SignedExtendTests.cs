using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;

namespace PyMCU.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for fixtures/avr/signed-extend.
///
/// Verifies that widening int8 to int16 uses sign-extension (not zero-extension).
///
/// Bug: int16(-5) zero-extended gives 0x00FB; high byte = 0x00 (wrong).
/// Fix: int16(-5) sign-extended gives 0xFFFB; high byte = 0xFF (correct).
///
/// Data-space addresses (ATmega328P):
///   GPIOR0=0x3E, GPIOR1=0x4A, GPIOR2=0x4B, OCR0A=0x47
/// </summary>
[TestFixture]
public class SignedExtendTests
{
    private const int Gpior0 = 0x3E;
    private const int Gpior1 = 0x4A;
    private const int Gpior2 = 0x4B;
    private const int Ocr0A  = 0x47;

    private string _hex = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _hex = PymcuCompiler.BuildFixture("signed-extend");

    private ArduinoUnoSimulation Boot()
    {
        var uno = new ArduinoUnoSimulation();
        uno.WithHex(_hex);
        uno.RunToBreak();
        return uno;
    }

    [Test]
    public void NegFive_Low_Is0xFB() =>
        Boot().Data[Gpior0].Should().Be(0xFB, "int16(-5) low byte = 0xFB");

    [Test]
    public void NegFive_High_Is0xFF() =>
        Boot().Data[Gpior1].Should().Be(0xFF, "int16(-5) high byte = 0xFF (sign-extended), not 0x00 (zero-extended)");

    [Test]
    public void NegOneTwentyEight_Low_Is0x80() =>
        Boot().Data[Gpior2].Should().Be(0x80, "int16(-128) low byte = 0x80");

    [Test]
    public void NegOneTwentyEight_High_Is0xFF() =>
        Boot().Data[Ocr0A].Should().Be(0xFF, "int16(-128) high byte = 0xFF (sign-extended), not 0x00 (zero-extended)");
}

