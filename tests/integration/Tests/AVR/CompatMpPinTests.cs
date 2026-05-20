using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;

namespace PyMCU.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for fixtures/avr/compat-mp-pin.
/// Verifies that machine.Pin(13, Pin.OUT) from the pymcu-micropython compat
/// layer correctly maps Arduino Uno pin 13 to PB5 at compile time.
///
/// Fixture: Pin(13, Pin.OUT) blink -- 1000 ms HIGH, 1000 ms LOW, repeat.
/// </summary>
[TestFixture]
public class CompatMpPinTests
{
    private SimSession _session = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _session = new SimSession(PymcuCompiler.BuildFixture("compat-mp-pin"));

    [Test]
    public void Led_StartsHighAfterBoot()
    {
        var uno = Sim();
        uno.RunMilliseconds(10);
        uno.PortB.Should().HavePinHigh(5, "PB5 (D13) driven high on first led.on() call");
    }

    [Test]
    public void Led_StillHighBefore1Second()
    {
        var uno = Sim();
        uno.RunMilliseconds(900);
        uno.PortB.Should().HavePinHigh(5, "LED stays high until delay_ms(1000) expires");
    }

    [Test]
    public void Led_GoesLowAfterFirstDelay()
    {
        var uno = Sim();
        uno.RunMilliseconds(1100);
        uno.PortB.Should().HavePinLow(5, "LED goes low after first delay_ms(1000) expires");
    }

    [Test]
    public void Led_GoesHighAgainAfterSecondDelay()
    {
        var uno = Sim();
        uno.RunMilliseconds(2200);
        uno.PortB.Should().HavePinHigh(5, "LED cycles high again after second delay_ms(1000)");
    }

    [Test]
    public void Led_IsLowMidwayThroughSecondDelay()
    {
        var uno = Sim();
        uno.RunMilliseconds(1500);
        uno.PortB.Should().HavePinLow(5, "LED is low mid-way through second delay_ms(1000)");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private ArduinoUnoSimulation Sim() => _session.Reset();
}
