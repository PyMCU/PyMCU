using Avr8Sharp.TestKit;
using Avr8Sharp.TestKit.Boards;
using FluentAssertions;
using NUnit.Framework;

namespace PyMCU.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for fixtures/avr/sleep-float.
/// Verifies that sleep(float) folds to delay_ms(int) at compile time
/// and that the resulting delays match the expected duration.
/// </summary>
[TestFixture]
public class SleepFloatTests
{
    private SimSession _session = null!;

    [OneTimeSetUp]
    public void BuildFirmware() =>
        _session = new SimSession(PymcuCompiler.BuildFixture("sleep-float"));

    private ArduinoUnoSimulation Boot()
    {
        var uno = _session.Reset();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("R"), maxMs: 50);
        return uno;
    }

    // ── Compilation sanity ────────────────────────────────────────────────────

    [Test]
    public void Ready_ReceivedImmediately()
    {
        // If sleep(float) fails to compile, this test never passes.
        Boot().Serial.Text.Should().Contain("R");
    }

    // ── sleep(0.5) → ~500ms ──────────────────────────────────────────────────

    [Test]
    public void A_NotSentBefore400ms()
    {
        var uno = Boot();
        uno.RunMilliseconds(400);
        uno.Serial.Text.Should().NotContain("A", "sleep(0.5) should block for ~500ms");
    }

    [Test]
    public void A_SentBy650ms()
    {
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("A"), maxMs: 650);
        uno.Serial.Text.Should().Contain("A", "sleep(0.5) should complete within 650ms");
    }

    // ── sleep(1.5) → ~1500ms more ────────────────────────────────────────────

    [Test]
    public void B_NotSentBefore1800ms()
    {
        // 0.5 + 1.5 = 2.0s total; should not arrive before 1800ms
        var uno = Boot();
        uno.RunMilliseconds(1800);
        uno.Serial.Text.Should().NotContain("B");
    }

    [Test]
    public void B_SentBy2400ms()
    {
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("B"), maxMs: 2400);
        uno.Serial.Text.Should().Contain("B");
    }

    // ── sleep(1.0) → ~1000ms more ────────────────────────────────────────────

    [Test]
    public void C_SentBy3600ms()
    {
        // 0.5 + 1.5 + 1.0 = 3.0s total; allow 20% margin
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("C"), maxMs: 3600);
        uno.Serial.Text.Should().Contain("C");
    }
}
