using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;

namespace PyMCU.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for fixtures/avr/compat-mp-utime.
/// Verifies that utime.sleep_ms() from the pymcu-micropython compat layer
/// introduces the expected delay between UART writes.
///
/// Fixture sends 0x41 ('A') immediately, sleeps 500 ms, then sends 0x42 ('B').
/// </summary>
[TestFixture]
public class CompatMpUtimeTests
{
    private SimSession _session = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _session = new SimSession(PymcuCompiler.BuildFixture("compat-mp-utime"));

    [Test]
    public void ByteA_ArrivesBeforeSleep()
    {
        var uno = Sim();
        uno.RunUntilSerialBytes(uno.Serial, 1, maxMs: 50);
        uno.Serial.Bytes[0].Should().Be(0x41, "'A' sent before sleep_ms(500)");
    }

    [Test]
    public void ByteB_ArrivesAfterSleep()
    {
        var uno = Sim();
        uno.RunUntilSerialBytes(uno.Serial, 2, maxMs: 700);
        uno.Serial.Bytes[1].Should().Be(0x42, "'B' sent after sleep_ms(500)");
    }

    [Test]
    public void ByteB_NotReceivedBefore450ms()
    {
        var uno = Sim();
        uno.RunUntilSerialBytes(uno.Serial, 1, maxMs: 50);
        uno.RunMilliseconds(450);
        uno.Serial.ByteCount.Should().Be(1, "'B' must not be sent before sleep_ms(500) expires");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private ArduinoUnoSimulation Sim() => _session.Reset();
}
