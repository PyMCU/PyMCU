using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;
using AVR8Sharp.Core.Peripherals;

namespace Whipsnake.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for examples/avr/i2c-scanner.
/// Scans I²C addresses 0x01–0x7F. With no devices attached, finds 0 devices.
/// Boot: "I2C SCANNER\n", "Scanning 0x01-0x7F...\n". Finish: "Done. Found: 0\n"
/// </summary>
[TestFixture]
public class I2cScannerTests
{
    private string _hex = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _hex = PymcuCompiler.Build("i2c-scanner");

    [Test]
    public void Boot_SendsI2cScannerBanner()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "I2C SCANNER");
        uno.Serial.Should().ContainLine("I2C SCANNER");
    }

    [Test]
    public void Boot_SendsScanningMessage()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "Scanning 0x01-0x7F...", maxMs: 2000);
        uno.Serial.Should().Contain("Scanning 0x01-0x7F...");
    }

    [Test]
    public void Scan_CompletesWithNoDevicesFound()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "Found: 0", maxMs: 20000);
        uno.Serial.Should().Contain("Done. Found: 0");
    }

    private ArduinoUnoSimulation Sim()
    {
        var uno = new ArduinoUnoSimulation();
        uno.WithHex(_hex);
        uno.AddTwi(AvrTwi.TwiConfig, out _);
        return uno;
    }
}
