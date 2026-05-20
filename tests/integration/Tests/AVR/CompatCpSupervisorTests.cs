using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;

namespace PyMCU.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for fixtures/avr/compat-cp-supervisor.
/// Verifies that supervisor.ticks_ms() from the pymcu-circuitpython compat
/// layer compiles and returns 0 at boot (PyMCU uses a compile-time constant
/// stub — no hardware tick counter is maintained by default).
///
/// Fixture sends low byte of ticks_ms() result, then 0x44 ('D') done marker.
/// Expected UART: [0x00, 0x44].
/// </summary>
[TestFixture]
public class CompatCpSupervisorTests
{
    private SimSession _session = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _session = new SimSession(PymcuCompiler.BuildFixture("compat-cp-supervisor"));

    [Test]
    public void TicksMs_ReturnsZeroAtBoot()
    {
        var uno = Sim();
        uno.RunUntilSerialBytes(uno.Serial, 1, maxMs: 50);
        uno.Serial.Bytes[0].Should().Be(0x00, "supervisor.ticks_ms() stub returns 0 at boot");
    }

    [Test]
    public void DoneMarker_ReceivedAfterTicksMs()
    {
        var uno = Sim();
        uno.RunUntilSerialBytes(uno.Serial, 2, maxMs: 50);
        uno.Serial.Bytes[1].Should().Be(0x44, "'D' done marker sent after ticks_ms()");
    }

    [Test]
    public void ExactOutput_ZeroThenDone()
    {
        var uno = Sim();
        uno.RunUntilSerialBytes(uno.Serial, 2, maxMs: 50);
        uno.Serial.Should().HaveBytes([0x00, 0x44]);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private ArduinoUnoSimulation Sim() => _session.Reset();
}
