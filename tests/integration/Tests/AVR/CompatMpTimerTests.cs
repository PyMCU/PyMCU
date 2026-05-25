using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;

namespace PyMCU.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for fixtures/avr/compat-mp-timer.
/// Verifies machine.Timer(id, period, callback) MicroPython compatibility:
/// - Timer1 fires ~every 100 ms (CTC with prescaler 1024)
/// - The callback toggles PB5
/// </summary>
[TestFixture]
public class CompatMpTimerTests
{
    private SimSession _session = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _session = new SimSession(PymcuCompiler.BuildFixture("compat-mp-timer"));

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
        // First tick at ~100 ms sets PB5 HIGH; stop at 150 ms (before second tick at ~200 ms)
        uno.RunMilliseconds(150);
        uno.PortB.Should().HavePinHigh(5, "callback sets PB5 HIGH on first tick (~100 ms)");
    }

    [Test]
    public void Led_IsLowAfterSecondTick()
    {
        var uno = Sim();
        // Second tick at ~200 ms clears PB5
        uno.RunMilliseconds(250);
        uno.PortB.Should().HavePinLow(5, "callback sets PB5 LOW on second tick (~200 ms)");
    }

    private ArduinoUnoSimulation Sim() => _session.Reset();
}
