using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;

namespace PyMCU.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for fixtures/avr/signed-compare.
///
/// Verifies that signed int8 comparisons use BRLT/BRGE (signed branches)
/// instead of BRLO/BRSH (unsigned branches).
///
/// Bug case: -5 &lt; 1 with BRLO: 0xFB &lt; 0x01 = False (wrong).
/// Fix case: -5 &lt; 1 with BRLT: correct signed comparison = True.
///
/// Data-space addresses (ATmega328P):
///   GPIOR0=0x3E, GPIOR1=0x4A, GPIOR2=0x4B, OCR0A=0x47
/// </summary>
[TestFixture]
public class SignedCompareTests
{
    private const int Gpior0 = 0x3E;
    private const int Gpior1 = 0x4A;
    private const int Gpior2 = 0x4B;
    private const int Ocr0A  = 0x47;

    private SimSession _session = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _session = new SimSession(PymcuCompiler.BuildFixture("signed-compare"));

    private ArduinoUnoSimulation Boot()
    {
        var uno = _session.Reset();
        uno.RunToBreak();
        return uno;
    }

    [Test]
    public void NegFive_LT_One_IsTrue() =>
        Boot().Data[Gpior0].Should().Be(1, "signed: -5 < 1 must be true");

    [Test]
    public void NegOne_GT_NegTen_IsTrue() =>
        Boot().Data[Gpior1].Should().Be(1, "signed: -1 > -10 must be true");

    [Test]
    public void NegTen_LT_NegFive_IsTrue() =>
        Boot().Data[Gpior2].Should().Be(1, "signed: -10 < -5 must be true");

    [Test]
    public void One_GT_NegOne_IsTrue() =>
        Boot().Data[Ocr0A].Should().Be(1, "signed: 1 > -1 must be true");
}

