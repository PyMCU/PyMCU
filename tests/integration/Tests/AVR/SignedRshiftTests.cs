using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;

namespace PyMCU.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for fixtures/avr/signed-rshift.
///
/// Verifies that int8 right-shift uses ASR (arithmetic shift right),
/// which propagates the sign bit, rather than LSR (logical shift right),
/// which always shifts in a zero.
///
/// Bug: -8 >> 1 with LSR = 0x7C = 124 (sign lost).
/// Fix: -8 >> 1 with ASR = 0xFC = -4 (correct).
///
/// Data-space addresses (ATmega328P):
///   GPIOR0=0x3E, GPIOR1=0x4A, GPIOR2=0x4B
/// </summary>
[TestFixture]
public class SignedRshiftTests
{
    private const int Gpior0 = 0x3E;
    private const int Gpior1 = 0x4A;
    private const int Gpior2 = 0x4B;

    private SimSession _session = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _session = new SimSession(PymcuCompiler.BuildFixture("signed-rshift"));

    private ArduinoUnoSimulation Boot()
    {
        var uno = _session.Reset();
        uno.RunToBreak();
        return uno;
    }

    [Test]
    public void NegEight_Rshift1_Is_NegFour() =>
        Boot().Data[Gpior0].Should().Be(0xFC, "signed: -8 >> 1 = -4 (0xFC), not 0x7C with LSR");

    [Test]
    public void NegOnetwentyeight_Rshift7_Is_NegOne() =>
        Boot().Data[Gpior1].Should().Be(0xFF, "signed: -128 >> 7 = -1 (0xFF), not 0x01 with LSR");

    [Test]
    public void NegThirtytwo_Rshift3_Is_NegFour() =>
        Boot().Data[Gpior2].Should().Be(0xFC, "signed: -32 >> 3 = -4 (0xFC), not 0x04 with LSR");
}

