using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;

namespace Whisnake.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for examples/avr/timer-interrupt.
/// Timer1 OVF ISR (~1.049 s) sets GPIOR0[0]; main loop toggles LED and sends 'T\n'.
/// </summary>
[TestFixture]
public class TimerInterruptTests
{
    private string _hex = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _hex = PymcuCompiler.Build("timer-interrupt");

    [Test]
    public void Boot_SendsBanner()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "TIMER1 IRQ BLINK");
        uno.Serial.Should().ContainLine("TIMER1 IRQ BLINK");
    }

    [Test]
    public void After1Second_ToggleSent()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "TIMER1 IRQ BLINK");
        uno.RunUntilSerial(uno.Serial, "T\n", maxMs: 1200);
        uno.Serial.Should().Contain("T");
    }

    [Test]
    public void After1Second_LedHasToggled()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "TIMER1 IRQ BLINK");
        var ledBefore = uno.PortB.GetPinState(5);
        uno.RunUntilSerial(uno.Serial, "T\n", maxMs: 1200);
        var ledAfter = uno.PortB.GetPinState(5);
        ledAfter.Should().NotBe(ledBefore, "LED should toggle after first Timer1 overflow");
    }

    [Test]
    public void After2Seconds_TogglesSentTwice()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "TIMER1 IRQ BLINK");
        uno.RunUntilSerial(uno.Serial, s => s.Count(c => c == 'T') >= 2, maxMs: 2500);
        var tCount = uno.Serial.Text.Count(c => c == 'T');
        tCount.Should().BeGreaterThanOrEqualTo(2, "two Timer1 overflows should have fired");
    }

    private ArduinoUnoSimulation Sim()
    {
        var uno = new ArduinoUnoSimulation();
        uno.WithHex(_hex);
        return uno;
    }
}
