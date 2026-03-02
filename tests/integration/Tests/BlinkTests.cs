using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;

namespace PyMCU.IntegrationTests.Tests;

/// <summary>
/// Integration tests for examples/avr/blink.
/// Hardware: built-in LED on PB5. No button, no UART.
/// Logic: led.high() → delay_ms(1000) → led.low() → delay_ms(1000) → repeat.
/// </summary>
[TestFixture]
public class BlinkTests
{
    private string _hex = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _hex = PymcuCompiler.Build("blink");

    [Test]
    public void Led_StartsHighAfterBoot()
    {
        var uno = Sim();
        // SBI DDRB,5 + SBI PORTB,5 execute in the first few µs.
        uno.RunMilliseconds(10);
        uno.PortB.Should().HavePinHigh(5);
    }

    [Test]
    public void Led_IsStillHighBefore1Second()
    {
        var uno = Sim();
        // At 900 ms the first delay_ms(1000) has not yet completed.
        uno.RunMilliseconds(900);
        uno.PortB.Should().HavePinHigh(5);
    }

    [Test]
    public void Led_GoesLowAfterFirstDelay()
    {
        var uno = Sim();
        // The first delay_ms(1000) + loop overhead finishes well before 1100 ms.
        uno.RunMilliseconds(1100);
        uno.PortB.Should().HavePinLow(5);
    }

    [Test]
    public void Led_GoesHighAgainAfterSecondDelay()
    {
        var uno = Sim();
        // Two delays of 1000 ms each; LED should be high again by 2200 ms.
        uno.RunMilliseconds(2200);
        uno.PortB.Should().HavePinHigh(5);
    }

    [Test]
    public void Led_IsLowBetween1100msAnd2000ms()
    {
        var uno = Sim();
        // At 1500 ms we are mid-way through the second delay_ms(1000).
        uno.RunMilliseconds(1500);
        uno.PortB.Should().HavePinLow(5);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private ArduinoUnoSimulation Sim()
    {
        var uno = new ArduinoUnoSimulation();
        uno.WithHex(_hex);
        return uno;
    }
}
