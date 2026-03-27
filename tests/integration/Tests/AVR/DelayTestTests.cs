using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;

namespace PyMCU.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for examples/avr/delay-test.
///
/// Firmware sequence:
///   'R' (0x52) -- sent immediately on boot
///   delay_ms(1000)
///   'A' (0x41) -- sent after ~1000 ms
///   delay_ms(3000)
///   'B' (0x42) -- sent after ~4000 ms total (3000 > 255: exercises uint16 path)
///
/// These tests verify that delay_ms() with values > 255 works correctly now that
/// the loop counter is a uint16 (compiler fix: visitVarDecl inline prefix).
/// </summary>
[TestFixture]
public class DelayTestTests
{
    private string _hex = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _hex = PymcuCompiler.BuildFixture("delay-test");

    // ── Ready sentinel ─────────────────────────────────────────────────────────

    [Test]
    public void Boot_SendsReadySentinel()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "R", maxMs: 50);
        uno.Serial.Should().Contain("R");
    }

    // ── delay_ms(1000) — 16-bit, loops 1000 times ─────────────────────────────

    [Test]
    public void Delay1000ms_SentinelANotArrivedBefore900ms()
    {
        var uno = Sim();
        uno.RunMilliseconds(900);
        // 'A' should NOT be in the serial buffer yet.
        uno.Serial.Text.Should().NotContain("A",
            "delay_ms(1000) must not complete in less than 900 ms");
    }

    [Test]
    public void Delay1000ms_SentinelAArrivesBy1200ms()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "A", maxMs: 1200);
        uno.Serial.Should().Contain("A",
            "delay_ms(1000) should complete within 1200 ms of simulated time");
    }

    // ── delay_ms(3000) — 16-bit value > 255 ───────────────────────────────────

    [Test]
    public void Delay3000ms_SentinelBNotArrivedBefore3800ms()
    {
        var uno = Sim();
        // At 3.8 s total, only 'R' and 'A' should have arrived;
        // 'B' requires the 3000 ms second delay to also complete.
        uno.RunMilliseconds(3800);
        uno.Serial.Text.Should().NotContain("B",
            "delay_ms(3000) must not complete in less than 3800 ms after 'A'");
    }

    [Test]
    public void Delay3000ms_SentinelBArrivesBy4500ms()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "B", maxMs: 4500);
        uno.Serial.Should().Contain("B",
            "delay_ms(3000) (uint16: 3000 > 255) should complete within 4500 ms total");
    }

    [Test]
    public void Sequence_IsRThenAThenB()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "B", maxMs: 4500);
        var text = uno.Serial.Text;
        var rIdx = text.IndexOf('R');
        var aIdx = text.IndexOf('A');
        var bIdx = text.IndexOf('B');

        rIdx.Should().BeGreaterThanOrEqualTo(0, "'R' must appear");
        aIdx.Should().BeGreaterThan(rIdx, "'A' must appear after 'R'");
        bIdx.Should().BeGreaterThan(aIdx, "'B' must appear after 'A'");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private ArduinoUnoSimulation Sim()
    {
        var uno = new ArduinoUnoSimulation();
        uno.WithHex(_hex);
        return uno;
    }
}
