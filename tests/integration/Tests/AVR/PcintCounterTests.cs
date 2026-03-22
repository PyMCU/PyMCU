using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;

namespace Whisnake.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for examples/avr/pcint-counter.
/// Verifies PCINT0 vector (byte 0x0006 / word 0x0003) dispatches correctly
/// when a pin change occurs on PB0 (Arduino digital pin 8).
/// Only falling edges (button press = low) increment the counter.
/// </summary>
[TestFixture]
public class PcintCounterTests
{
    private string _hex = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _hex = PymcuCompiler.Build("pcint-counter");

    [Test]
    public void Boot_SendsBanner()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "PCINT COUNTER");
        uno.Serial.Should().ContainLine("PCINT COUNTER");
    }

    [Test]
    public void SinglePress_SendsCount01()
    {
        // PCINT0 ISR (word 0x0003): if address were wrong, no ISR would fire.
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "PCINT COUNTER\n");

        // Simulate button press: PB0 falls low (active-low button)
        uno.PortB.SetPinValue(0, false);
        uno.RunUntilSerial(uno.Serial, s => s.Contains("COUNT:01"), maxMs: 100);

        uno.Serial.Text.Should().Contain("COUNT:01", "first press must output COUNT:01");
    }

    [Test]
    public void ReleaseOnly_DoesNotIncrementCount()
    {
        // Rising edge (button release) triggers PCINT0 ISR, but the ISR
        // flag check in main reads PB0 == 1 (high) so count must not change.
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "PCINT COUNTER\n");

        // Start with button held down (PB0 already low due to press)
        uno.PortB.SetPinValue(0, false);
        uno.RunUntilSerial(uno.Serial, s => s.Contains("COUNT:"), maxMs: 50);

        var afterPress = uno.Serial.ByteCount;

        // Now release: PB0 goes high — PCINT0 fires again but should NOT count
        uno.PortB.SetPinValue(0, true);
        uno.RunMilliseconds(20);

        // No new COUNT: line should have appeared
        uno.Serial.Text[afterPress..].Should().NotContain("COUNT:",
            "releasing the button must not increment the counter");
    }

    [Test]
    public void ThreePresses_SendsCount03()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "PCINT COUNTER\n");

        // Three press-release cycles
        for (int i = 0; i < 3; i++)
        {
            uno.PortB.SetPinValue(0, false);   // press
            uno.RunMilliseconds(10);
            uno.PortB.SetPinValue(0, true);    // release
            uno.RunMilliseconds(10);
        }

        uno.RunUntilSerial(uno.Serial, s => s.Contains("COUNT:03"), maxMs: 500);
        uno.Serial.Text.Should().Contain("COUNT:03", "three presses must output COUNT:03");
    }

    private ArduinoUnoSimulation Sim()
    {
        var uno = new ArduinoUnoSimulation();
        uno.WithHex(_hex);
        // PB0 starts high (button released, active-low with pull-up)
        uno.PortB.SetPinValue(0, true);
        return uno;
    }
}
