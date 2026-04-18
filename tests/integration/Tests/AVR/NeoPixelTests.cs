using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;

namespace PyMCU.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for examples/avr/neopixel.
/// NeoPixel (WS2812B) single-pixel driver on PD6.
/// The AVR8Sharp simulator does not simulate WS2812 protocol output, so
/// tests verify: pin configured as output, boot banner received, and
/// UART phase bytes are sent in the correct cycle (0->1->2->0->...).
/// </summary>
[TestFixture]
public class NeoPixelTests
{
    private SimSession _session = null!;

    // ATmega328P DDRD data-space address (controls pin direction for port D)
    private const int DDRD = 0x2A;

    [OneTimeSetUp]
    public void BuildFirmware() => _session = new SimSession(PymcuCompiler.Build("neopixel"));

    [Test]
    public void Boot_SendsBanner()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "NEO");
        uno.Serial.Should().ContainLine("NEO");
    }

    [Test]
    public void Init_Pd6_ConfiguredAsOutput()
    {
        // ws2812_init("PD6") sets DDRD[6]=1; DDRD bit 6 must be set after init.
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "NEO");
        uno.RunMilliseconds(10);
        var ddrd = uno.Data[DDRD];
        (ddrd & 0x40).Should().Be(0x40, "DDRD bit 6 (PD6) must be set as output for NeoPixel data line");
    }

    [Test]
    public void FirstPhase_Sends0_Red()
    {
        // Phase 0 = Red; byte 0 is sent after "NEO\n"
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "NEO\n");
        var before = uno.Serial.ByteCount;
        uno.RunUntilSerialBytes(uno.Serial, before + 2, maxMs: 1000);
        uno.Serial.Bytes[before].Should().Be(0, "phase 0 = Red sends byte 0");
        uno.Serial.Bytes[before + 1].Should().Be((byte)'\n');
    }

    [Test]
    public void SecondPhase_Sends1_Green()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "NEO\n");
        var before = uno.Serial.ByteCount;
        uno.RunUntilSerialBytes(uno.Serial, before + 4, maxMs: 2000);
        uno.Serial.Bytes[before + 2].Should().Be(1, "phase 1 = Green sends byte 1");
    }

    [Test]
    public void ThirdPhase_Sends2_Blue()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "NEO\n");
        var before = uno.Serial.ByteCount;
        uno.RunUntilSerialBytes(uno.Serial, before + 6, maxMs: 3000);
        uno.Serial.Bytes[before + 4].Should().Be(2, "phase 2 = Blue sends byte 2");
    }

    [Test]
    public void CycleWraps_FourthPhase_Sends0_Again()
    {
        // After R/G/B the phase wraps back to 0
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "NEO\n");
        var before = uno.Serial.ByteCount;
        uno.RunUntilSerialBytes(uno.Serial, before + 8, maxMs: 4000);
        uno.Serial.Bytes[before + 6].Should().Be(0, "phase wraps back to 0 after 3 colors");
    }

    private ArduinoUnoSimulation Sim() => _session.Reset();
}
