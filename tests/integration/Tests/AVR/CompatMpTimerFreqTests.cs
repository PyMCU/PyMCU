using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;

namespace PyMCU.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for fixtures/avr/compat-mp-timer-freq.
///
/// Verifies MicroPython Timer freq= API:
///   Timer(1, freq=10, callback=fn)   -- 10 Hz == 100 ms period
///
/// This is the standard MicroPython way to configure a timer by frequency
/// rather than period.  The prescaler is automatically selected so the
/// OCR value fits in a 16-bit register.
///
/// For freq=10 Hz at 16 MHz, prescaler=64 is selected:
///   OCR = 250 000 / 10 - 1 = 24 999 (0x61A7)
///   Period = 25 000 × 64 / 16 000 000 = 100 ms (exact)
///
/// Fixture: Timer(1, freq=10, callback=on_tick)
///   on_tick toggles led_state; main loop drives PB5 from led_state.
/// </summary>
[TestFixture]
public class CompatMpTimerFreqTests
{
    private SimSession _session = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _session = new SimSession(PymcuCompiler.BuildFixture("compat-mp-timer-freq"));

    [Test]
    public void Led_IsLowAtStart()
    {
        var uno = Sim();
        uno.RunMilliseconds(10);
        uno.PortB.Should().HavePinLow(5, "LED should start LOW before first timer tick");
    }

    [Test]
    public void Led_IsHighAfterFirstTick()
    {
        var uno = Sim();
        // First tick at ~100 ms; check at 150 ms (between first and second tick)
        uno.RunMilliseconds(150);
        uno.PortB.Should().HavePinHigh(5, "freq=10 Hz fires at ~100 ms, toggling PB5 HIGH");
    }

    [Test]
    public void Led_IsLowAfterSecondTick()
    {
        var uno = Sim();
        // Second tick at ~200 ms; check at 250 ms
        uno.RunMilliseconds(250);
        uno.PortB.Should().HavePinLow(5, "second tick at ~200 ms toggles PB5 back LOW");
    }

    [Test]
    public void Led_MatchesPeriodEquivalent()
    {
        // freq=10 Hz == period=100 ms -- verify timing aligns with the period= variant.
        // Both should produce identical OCR1A = 24 999 (prescaler 64).
        var uno = Sim();
        uno.RunMilliseconds(550);
        // 5 ticks: HIGH, LOW, HIGH, LOW, HIGH
        uno.PortB.Should().HavePinHigh(5, "5th tick at ~500 ms sets PB5 HIGH again");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private ArduinoUnoSimulation Sim() => _session.Reset();
}
