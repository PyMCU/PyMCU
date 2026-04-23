using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;
using AVR8Sharp.Core.Peripherals;

namespace PyMCU.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for examples/avr/ssd1306.
/// SSD1306 128x64 OLED display driver over I2C (address 0x3C).
/// Hardware: SDA=PC4 (A4), SCL=PC5 (A5).
///
/// Expected UART output:
///   "OLED\n"   -- boot banner (printed before oled.init())
///   "OK\n"     -- init complete (only reached when a real device is present)
///
/// Note: the driver uses the raw i2c.write() API without NACK guards, so the
/// I2C init sequence hangs in simulation when no device is on the bus.
/// Tests are limited to the boot banner, which is printed before init.
/// </summary>
[TestFixture]
public class Ssd1306Tests
{
    private SimSession _session = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _session = new SimSession(PymcuCompiler.Build("ssd1306"));

    [Test]
    public void Boot_SendsBanner()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "OLED\n", maxMs: 300);
        uno.Serial.Should().ContainLine("OLED");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private ArduinoUnoSimulation Sim() => _session.Reset();
}
