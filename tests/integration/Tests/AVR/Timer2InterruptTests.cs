using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;

namespace PyMCU.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for examples/avr/timer2-interrupt.
/// Verifies Timer2 OVF vector (byte 0x0012 / word 0x0009) dispatches correctly.
/// Timer2 at prescaler 1024: 256 * 1024 / 16 MHz = 16.384 ms per overflow.
/// 61 overflows = ~999 ms per LED toggle + "T2\n" UART output.
/// </summary>
[TestFixture]
public class Timer2InterruptTests
{
    private string _hex = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _hex = PymcuCompiler.Build("timer2-interrupt");

    [Test]
    public void Boot_SendsBanner()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "TIMER2 IRQ BLINK");
        uno.Serial.Should().ContainLine("TIMER2 IRQ BLINK");
    }

    [Test]
    public void After1Second_ToggleSent()
    {
        // Timer2 OVF at word 0x0009: if the address were wrong (e.g. 0x0012 as word),
        // the ISR would never run and "T2\n" would never arrive.
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "TIMER2 IRQ BLINK");
        uno.RunUntilSerial(uno.Serial, s => s.Contains("T2"), maxMs: 1200);
        uno.Serial.Text.Should().Contain("T2", "Timer2 OVF ISR must fire within ~1 s");
    }

    [Test]
    public void After1Second_LedHasToggled()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "TIMER2 IRQ BLINK");
        var ledBefore = uno.PortB.GetPinState(5);
        uno.RunUntilSerial(uno.Serial, s => s.Contains("T2"), maxMs: 1200);
        var ledAfter = uno.PortB.GetPinState(5);
        ledAfter.Should().NotBe(ledBefore, "LED on PB5 must toggle after first Timer2 overflow count");
    }

    [Test]
    public void After2Seconds_TwoTogglesSent()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "TIMER2 IRQ BLINK");
        uno.RunUntilSerial(uno.Serial, s => s.Count(c => c == 'T') >= 2, maxMs: 2500);
        var tCount = uno.Serial.Text.Count(c => c == 'T');
        tCount.Should().BeGreaterThanOrEqualTo(2, "two Timer2 overflow groups should fire within 2.5 s");
    }

    private ArduinoUnoSimulation Sim()
    {
        var uno = new ArduinoUnoSimulation();
        uno.WithHex(_hex);
        return uno;
    }
}
