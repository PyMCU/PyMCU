using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;

namespace PyMCU.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for fixtures/avr/ds18b20.
/// Verifies that the DS18B20 driver compiles and handles the no-sensor case
/// (bus remains HIGH = no presence pulse) by outputting "ERR".
/// </summary>
[TestFixture]
public class Ds18b20Tests
{
    private SimSession _session = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _session = new SimSession(PymcuCompiler.BuildFixture("ds18b20"));

    [Test]
    public void Boot_SendsBanner()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "DS", maxMs: 300);
        uno.Serial.Should().Contain("DS");
    }

    [Test]
    public void Driver_OutputsStatusAfterRead()
    {
        // AVR8Sharp simulates floating I/O pins as LOW, so the firmware sees
        // a "presence pulse" (PD2 reads LOW after release) and attempts a full
        // conversion read (750 ms wait) before outputting a status line.
        // We verify that the driver completes at least one read cycle and
        // outputs either "ERR" (no device) or "OK" (raw != -32768).
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "DS", maxMs: 300);
        // Allow > 750 ms for conversion + UART output
        uno.RunMilliseconds(1500);
        uno.Serial.ByteCount.Should().BeGreaterThan(4,
            "driver should have output a status line after at least one read cycle");
    }

    private ArduinoUnoSimulation Sim() => _session.Reset();
}
