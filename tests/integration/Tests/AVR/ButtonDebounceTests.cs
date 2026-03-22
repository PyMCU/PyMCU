using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;

namespace Whisnake.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for examples/avr/button-debounce.
/// Each button press increments a uint16 counter and sends 2 bytes big-endian.
/// At 1000 presses: counter resets and 'R' (0x52) is sent.
/// </summary>
[TestFixture]
public class ButtonDebounceTests
{
    private string _hex = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _hex = PymcuCompiler.Build("button-debounce");

    [Test]
    public void SinglePress_SendsCountOne()
    {
        var uno = Sim();
        uno.RunMilliseconds(20); // settle
        Press(uno);
        uno.RunUntilSerialBytes(uno.Serial, 2, maxMs: 100);
        // count=1 big-endian: 0x00, 0x01
        uno.Serial.Bytes[0].Should().Be(0x00);
        uno.Serial.Bytes[1].Should().Be(0x01);
    }

    [Test]
    public void SinglePress_TogglesLed()
    {
        var uno = Sim();
        uno.RunMilliseconds(20);
        var ledBefore = uno.PortB.GetPinState(5);
        Press(uno);
        uno.RunMilliseconds(30);
        var ledAfter = uno.PortB.GetPinState(5);
        ledAfter.Should().NotBe(ledBefore, "LED toggles on each press");
    }

    [Test]
    public void ThreePresses_SendsThreeCountPairs()
    {
        var uno = Sim();
        uno.RunMilliseconds(20);
        for (var i = 0; i < 3; i++)
        {
            Press(uno);
            uno.RunMilliseconds(30);
        }
        uno.RunUntilSerialBytes(uno.Serial, 6, maxMs: 300);
        // count 1,2,3 big-endian
        uno.Serial.Bytes[0].Should().Be(0); uno.Serial.Bytes[1].Should().Be(1);
        uno.Serial.Bytes[2].Should().Be(0); uno.Serial.Bytes[3].Should().Be(2);
        uno.Serial.Bytes[4].Should().Be(0); uno.Serial.Bytes[5].Should().Be(3);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void Press(ArduinoUnoSimulation uno)
    {
        uno.PortD.SetPinValue(2, false); // falling edge (press)
        uno.RunMilliseconds(15);
        uno.PortD.SetPinValue(2, true);  // release
        uno.RunMilliseconds(15);
    }

    private ArduinoUnoSimulation Sim()
    {
        var uno = new ArduinoUnoSimulation();
        uno.WithHex(_hex);
        uno.PortD.SetPinValue(2, true); // released
        return uno;
    }
}
