using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;

namespace PyMCU.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for the no-entrypoint fixture.
/// Verifies that a script with no <c>def main():</c> wrapper compiles
/// correctly and produces the expected runtime behaviour.  The compiler
/// synthesizes a <c>main</c> function automatically from the top-level
/// executable statements.
///
/// Expected serial output: "NOMAIN\n" at boot, then "THREE\n" after every
/// third blink cycle.
/// </summary>
[TestFixture]
public class NoEntrypointTests
{
    private SimSession _session = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _session = new SimSession(PymcuCompiler.BuildFixture("no-entrypoint"));

    [Test]
    public void Boot_SendsBanner()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "NOMAIN", maxMs: 200);
        uno.Serial.Should().ContainLine("NOMAIN");
    }

    [Test]
    public void Led_GoesHighOnFirstBlink()
    {
        var uno = Sim();
        // After the UART banner the loop starts: led.high() fires immediately.
        // At 200 ms we are well within the first 500 ms high phase.
        uno.RunMilliseconds(200);
        uno.PortB.Should().HavePinHigh(5);
    }

    [Test]
    public void Led_GoesLowAfterFirstHighDelay()
    {
        var uno = Sim();
        // led.low() fires ~500 ms after boot.  At 700 ms we are inside the
        // 500 ms low phase (between 500 ms and 1000 ms).
        uno.RunMilliseconds(700);
        uno.PortB.Should().HavePinLow(5);
    }

    [Test]
    public void ThreeBlinks_SendsThreeBanner()
    {
        // Each cycle is ~1000 ms (500 ms high + 500 ms low).
        // After 3 cycles (count reaches 3) the firmware prints "THREE\n".
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "THREE", maxMs: 4000);
        uno.Serial.Text.Should().Contain("THREE");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private ArduinoUnoSimulation Sim() => _session.Reset();
}
