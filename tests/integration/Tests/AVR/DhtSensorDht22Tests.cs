using AVR8Sharp.Core.Peripherals;
using Avr8Sharp.TestKit.Boards;
using FluentAssertions;
using NUnit.Framework;

namespace PyMCU.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for the dht-sensor-dht22 fixture.
///
/// DHT22 driver: humidity() and temperature() return float values decoded from
/// 16-bit fixed-point wire data (value = raw / 10.0).  The firmware prints them
/// via uart_write_float (one decimal place).
///
/// Boot banner:  "DHT22 ready\n"
/// Error line:   "read error\n"
/// OK line:      "H: 55.0  T: 23.5\n"  (sep="" so all on one line)
/// </summary>
[TestFixture]
public class DhtSensorDht22Tests
{
    private static SimSession _session = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _session = new SimSession(PymcuCompiler.BuildFixture("dht-sensor-dht22"));

    // ── Boot helper ───────────────────────────────────────────────────────────

    private ArduinoUnoSimulation Boot()
    {
        var uno = _session.Reset();
        uno.PortD.SetPinValue(2, true); // idle HIGH (pull-up / no sensor)
        uno.RunUntilSerial(uno.Serial, "DHT22 ready\n", maxMs: 500);
        return uno;
    }

    // ── Boot tests ────────────────────────────────────────────────────────────

    [Test]
    public void Boot_SendsBanner()
    {
        var uno = Boot();
        uno.Serial.Text.Should().Contain("DHT22 ready");
    }

    // ── No-sensor tests ───────────────────────────────────────────────────────

    [Test]
    public void NoSensor_OutputsReadError()
    {
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("read error"), maxMs: 200);
        uno.Serial.Text.Should().Contain("read error",
            "no sensor means time_pulse_us timeout → failed=True → read error");
    }

    // ── With-sensor tests ─────────────────────────────────────────────────────

    [Test]
    public void WithSensor_OutputsHumidity()
    {
        // 550 tenths = 55.0 %
        var uno    = Boot();
        var sensor = new Dht22Simulator(uno, uno.PortD, 2);

        sensor.Respond(humidityTenths: 550, temperatureTenths: 235);

        try { uno.RunUntilSerial(uno.Serial, s => s.Contains("H: 55.0"), maxMs: 200); } catch {}
        TestContext.Out.WriteLine($"Serial: '{uno.Serial.Text}'");
        uno.Serial.Text.Should().Contain("H: 55.0");
    }

    [Test]
    public void WithSensor_OutputsTemperature()
    {
        // 235 tenths = 23.5 °C
        var uno    = Boot();
        var sensor = new Dht22Simulator(uno, uno.PortD, 2);

        sensor.Respond(humidityTenths: 550, temperatureTenths: 235);

        uno.RunUntilSerial(uno.Serial, s => s.Contains("T: 23.5"), maxMs: 200);
        uno.Serial.Text.Should().Contain("T: 23.5");
    }

    [Test]
    public void WithSensor_NegativeTemperature()
    {
        // -50 tenths = -5.0 °C
        var uno    = Boot();
        var sensor = new Dht22Simulator(uno, uno.PortD, 2);

        sensor.Respond(humidityTenths: 550, temperatureTenths: -50);

        uno.RunUntilSerial(uno.Serial, s => s.Contains("T: -5.0"), maxMs: 200);
        uno.Serial.Text.Should().Contain("T: -5.0");
    }

    [Test]
    public void WithSensor_BadChecksum_OutputsReadError()
    {
        var uno    = Boot();
        var sensor = new Dht22Simulator(uno, uno.PortD, 2);

        sensor.RespondWithBadChecksum(humidityTenths: 550, temperatureTenths: 235);

        uno.RunUntilSerial(uno.Serial, s => s.Contains("read error"), maxMs: 200);
        uno.Serial.Text.Should().Contain("read error",
            "bad checksum → driver sets failed=True → read error");
    }
}
