using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;

namespace Whipsnake.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for examples/avr/timer-poll.
/// Timer0 at prescaler 256 → overflow every 4.096 ms; 244 overflows ≈ 1 s.
/// On each 1-second tick: LED toggles, UART sends 'T' + '\n'.
/// </summary>
[TestFixture]
public class TimerPollTests
{
    private string _hex = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _hex = PymcuCompiler.Build("timer-poll");

    [Test]
    public void After1Second_LedToggles()
    {
        var uno = Sim();
        uno.RunMilliseconds(10); // init
        var ledBefore = uno.PortB.GetPinState(5);
        uno.RunUntilSerial(uno.Serial, "T", maxMs: 1200);
        var ledAfter = uno.PortB.GetPinState(5);
        ledAfter.Should().NotBe(ledBefore, "LED should toggle after ~1 s");
    }

    [Test]
    public void After1Second_UartSendsT()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "T", maxMs: 1200);
        uno.Serial.Should().Contain("T");
    }

    [Test]
    public void After2Seconds_TwoToggles()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, s => s.Count(c => c == 'T') >= 2, maxMs: 2500);
        uno.Serial.Text.Count(c => c == 'T').Should().BeGreaterThanOrEqualTo(2);
    }

    private ArduinoUnoSimulation Sim()
    {
        var uno = new ArduinoUnoSimulation();
        uno.WithHex(_hex);
        return uno;
    }
}
