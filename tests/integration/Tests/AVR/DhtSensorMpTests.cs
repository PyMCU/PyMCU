using AVR8Sharp.Core.Peripherals;
using Avr8Sharp.TestKit.Boards;
using FluentAssertions;
using NUnit.Framework;

namespace PyMCU.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for the dht-sensor-mp fixture.
///
/// MicroPython-style DHT11 driver using machine.Pin and machine.time_pulse_us.
///
/// Boot banner: "DHT11 ready\n"
/// Error line:  "read error\n"
/// OK:          "H: XX\n  T: XX\n\n"   (print_byte appends \n each time)
///
/// The "no sensor" group leaves PD2 floating (pull-up → HIGH).  The firmware
/// drives the start pulse, gets no ACK, and outputs "read error\n".
///
/// The "with sensor" group uses <see cref="Dht11Simulator"/> to inject a
/// well-timed DHT11 response, verifying the full decode/checksum/output path.
/// </summary>
[TestFixture]
public class DhtSensorMpTests
{
    private static SimSession _session = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _session = new SimSession(PymcuCompiler.BuildFixture("dht-sensor-mp"));

    // ── Boot helper ───────────────────────────────────────────────────────────

    /// <summary>
    /// Boots the simulation and waits for the banner.
    /// PD2 starts HIGH (pull-up / no sensor).
    /// </summary>
    private ArduinoUnoSimulation Boot()
    {
        var uno = _session.Reset();
        uno.PortD.SetPinValue(2, true); // idle HIGH
        uno.RunUntilSerial(uno.Serial, "DHT11 ready\n", maxMs: 500);
        return uno;
    }

    // ── Boot tests ────────────────────────────────────────────────────────────

    [Test]
    public void Boot_SendsBanner()
    {
        var uno = Boot();
        uno.Serial.Text.Should().Contain("DHT11 ready");
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

    [Test]
    public void NoSensor_RetriesAfterDelay()
    {
        var uno = Boot();
        // Two full retry cycles: banner + 2 × (measure → read error → delay_ms(2000))
        uno.RunUntilSerial(uno.Serial, s => s.Count(c => c == '\n') >= 3, maxMs: 5000);
        var errCount = uno.Serial.Text.Split('\n').Count(line => line.Contains("read error"));
        errCount.Should().BeGreaterThanOrEqualTo(2,
            "firmware retries every 2 s → at least two read error lines in ~4 s");
    }

    // ── With-sensor tests ─────────────────────────────────────────────────────

    [Test]
    public void WithSensor_OutputsHumidity()
    {
        var uno    = Boot();
        var sensor = new Dht11Simulator(uno, uno.PortD, 2);

        sensor.Respond(humidity: 55, temperature: 23);

        uno.RunUntilSerial(uno.Serial, s => s.Contains("H: 55"), maxMs: 200);
        uno.Serial.Text.Should().Contain("H: 55");
    }

    [Test]
    public void WithSensor_OutputsTemperature()
    {
        var uno    = Boot();
        var sensor = new Dht11Simulator(uno, uno.PortD, 2);

        sensor.Respond(humidity: 55, temperature: 23);

        uno.RunUntilSerial(uno.Serial, s => s.Contains("T: 23"), maxMs: 200);
        uno.Serial.Text.Should().Contain("T: 23");
    }

    [Test]
    public void WithSensor_LedHighOnSuccess()
    {
        var uno    = Boot();
        var sensor = new Dht11Simulator(uno, uno.PortD, 2);

        sensor.Respond(humidity: 40, temperature: 20);

        // Wait until temperature line is transmitted, then advance a bit so
        // led.high() (called after all UART writes) executes.
        uno.RunUntilSerial(uno.Serial, s => s.Contains("T: 20"), maxMs: 200);
        uno.RunMilliseconds(5);
        uno.PortB.GetPinState(5).Should().Be(PinState.High,
            "led.high() is called after a successful reading");
    }

    [Test]
    public void WithSensor_BadChecksum_OutputsReadError()
    {
        var uno    = Boot();
        var sensor = new Dht11Simulator(uno, uno.PortD, 2);

        sensor.RespondWithBadChecksum(humidity: 55, temperature: 23);

        uno.RunUntilSerial(uno.Serial, s => s.Contains("read error"), maxMs: 200);
        uno.Serial.Text.Should().Contain("read error",
            "bad checksum → measure() sets failed=True");
        uno.Serial.Text.Should().NotContain("H: ",
            "no output when checksum fails");
    }

    [Test]
    public void WithSensor_ExtremeValues_Decoded()
    {
        var uno    = Boot();
        var sensor = new Dht11Simulator(uno, uno.PortD, 2);

        // 0% humidity, 0°C — all 40 bits are '0', checksum = 0
        sensor.Respond(humidity: 0, temperature: 0);

        uno.RunUntilSerial(uno.Serial, s => s.Contains("T: 0"), maxMs: 200);
        uno.Serial.Text.Should().Contain("H: 0");
        uno.Serial.Text.Should().Contain("T: 0");
    }
}
