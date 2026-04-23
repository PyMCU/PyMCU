using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;
using AVR8Sharp.Core.Peripherals;

namespace PyMCU.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for examples/avr/bmp280.
/// BMP280 temperature and pressure sensor driver over I2C (address 0x76).
/// Hardware: SDA=PC4 (A4), SCL=PC5 (A5).
///
/// Expected UART output:
///   "BMP280\n"     -- boot banner (printed before bmp.init())
///   "OK\n"         -- init complete (only reached when a real device is present)
///
/// Note: the driver uses the raw i2c.write() API without NACK guards, so the
/// I2C init and read sequences hang in simulation when no device is on the bus.
/// Tests are limited to the boot banner, which is printed before init.
/// </summary>
[TestFixture]
public class Bmp280Tests
{
    private SimSession _session = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _session = new SimSession(PymcuCompiler.Build("bmp280"));

    [Test]
    public void Boot_SendsBanner()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "BMP280\n", maxMs: 300);
        uno.Serial.Should().ContainLine("BMP280");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private ArduinoUnoSimulation Sim() => _session.Reset();
}
