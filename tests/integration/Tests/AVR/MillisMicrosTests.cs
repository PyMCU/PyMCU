using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;

namespace PyMCU.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for fixtures/avr/millis-micros.
///
/// Verifies that millis_init() configures Timer0 with prescaler 64 and that
/// millis() counts Timer0 overflow events correctly.  Each overflow at
/// 16 MHz / prescaler 64 fires every 256 * 64 / 16_000_000 = 1.024 ms.
///
/// Checkpoint 1: firmware busy-waits until millis() >= 10, then BREAKs.
///   GPIOR0 = millis() low byte  (should be >= 10)
///   GPIOR1 = millis() high byte (should be 0 for small values)
///
/// Checkpoint 2: firmware busy-waits until millis() >= 50, then BREAKs.
///   GPIOR0 = millis() low byte  (should be >= 50)
///
/// Data-space addresses (ATmega328P):
///   GPIOR0 = 0x3E   GPIOR1 = 0x4A
/// </summary>
[TestFixture]
public class MillisMicrosTests
{
    private const int Gpior0Addr = 0x3E;
    private const int Gpior1Addr = 0x4A;

    private SimSession _session = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _session = new SimSession(PymcuCompiler.BuildFixture("millis-micros"));

    private ArduinoUnoSimulation Boot() => _session.Reset();

    // ── Checkpoint 1: millis() >= 10 after 10 overflow cycles ──────────────

    [Test]
    public void Cp1_MillisAtLeast10()
    {
        var uno = Boot();
        uno.RunToBreak(maxInstructions: 50_000_000);
        var lo = uno.Data[Gpior0Addr];
        var hi = uno.Data[Gpior1Addr];
        int count = lo | (hi << 8);
        count.Should().BeGreaterThanOrEqualTo(10,
            "millis() must reach at least 10 before first BREAK");
    }

    [Test]
    public void Cp1_MillisHighByte_IsZero_ForSmallCount()
    {
        var uno = Boot();
        uno.RunToBreak(maxInstructions: 50_000_000);
        uno.Data[Gpior1Addr].Should().Be(0,
            "millis() high byte must be 0 when count is < 256");
    }

    // ── Checkpoint 2: millis() >= 50 after 50 overflow cycles ──────────────

    [Test]
    public void Cp2_MillisAtLeast50()
    {
        var uno = Boot();
        uno.RunToBreak(maxInstructions: 50_000_000);
        uno.RunInstructions(1);
        uno.RunToBreak(maxInstructions: 50_000_000);
        var lo = uno.Data[Gpior0Addr];
        var hi = uno.Data[Gpior1Addr];
        int count = lo | (hi << 8);
        count.Should().BeGreaterThanOrEqualTo(50,
            "millis() must reach at least 50 before second BREAK");
    }
}
