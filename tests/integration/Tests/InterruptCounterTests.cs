using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;

namespace PyMCU.IntegrationTests.Tests;

/// <summary>
/// Integration tests for examples/avr/interrupt-counter.
/// INT0 ISR (falling edge on PD2) increments a counter and sets GPIOR0[0].
/// Main loop detects the flag, clears it, toggles LED, and writes count byte.
/// </summary>
[TestFixture]
public class InterruptCounterTests
{
    private string _hex = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _hex = PymcuCompiler.Build("interrupt-counter");

    [Test]
    public void Boot_SendsIntCounterBanner()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "INT COUNTER");
        uno.Serial.Should().ContainLine("INT COUNTER");
    }

    [Test]
    public void ButtonPress_IncrementsCounter()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "INT COUNTER\n");
        var before = uno.Serial.ByteCount;

        // Falling edge on PD2 → INT0 fires
        uno.PortD.SetPinValue(2, true);
        uno.RunMilliseconds(1);
        uno.PortD.SetPinValue(2, false); // falling edge
        uno.RunMilliseconds(20);         // let ISR + main loop run

        uno.Serial.ByteCount.Should().BeGreaterThan(before, "one count byte should have been sent");
        // Count byte after the banner is 0x01 (count = 1)
        uno.Serial.Bytes.Skip(before).First().Should().Be(1);
    }

    [Test]
    public void TwoPresses_CountsCorrectly()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "INT COUNTER\n");
        var before = uno.Serial.ByteCount;

        for (var i = 0; i < 2; i++)
        {
            uno.PortD.SetPinValue(2, true);
            uno.RunMilliseconds(5);
            uno.PortD.SetPinValue(2, false); // falling edge
            uno.RunMilliseconds(20);
        }

        var countBytes = uno.Serial.Bytes.Skip(before).Take(2).ToArray();
        countBytes.Should().Equal([1, 2]);
    }

    [Test]
    public void ButtonPress_TogglesLed()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "INT COUNTER\n");
        var ledBefore = uno.PortB.GetPinState(5);

        uno.PortD.SetPinValue(2, true);
        uno.RunMilliseconds(1);
        uno.PortD.SetPinValue(2, false);
        uno.RunMilliseconds(20);

        var ledAfter = uno.PortB.GetPinState(5);
        ledAfter.Should().NotBe(ledBefore, "LED should toggle on each interrupt");
    }

    private ArduinoUnoSimulation Sim()
    {
        var uno = new ArduinoUnoSimulation();
        uno.WithHex(_hex);
        uno.PortD.SetPinValue(2, true); // button released initially
        return uno;
    }
}
