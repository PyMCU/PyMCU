using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using AVR8Sharp.Core.Peripherals;

namespace PyMCU.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for tests/integration/fixtures/avr/i2c-readfrom-mem.
/// Exercises I2C.writeto_mem(addr, reg, data) and I2C.readfrom_mem(addr, reg, buf, n).
/// With no I2C device attached both operations complete (with NACK) and the
/// firmware outputs "WM\n" after writeto_mem and "RM\n" after readfrom_mem.
/// </summary>
[TestFixture]
public class I2cReadfromMemTests
{
    private static string _hex = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _hex = PymcuCompiler.BuildFixture("i2c-readfrom-mem");

    private ArduinoUnoSimulation Sim()
    {
        var uno = new ArduinoUnoSimulation();
        uno.WithHex(_hex);
        uno.AddTwi(AvrTwi.TwiConfig, out _);
        return uno;
    }

    [Test]
    public void WritetoMem_CompletesAndEmitsMarker()
    {
        // writeto_mem sends START, SLA+W, reg, data, STOP.
        // With no device the SLA+W is NACK'd and the function returns 0,
        // but execution continues and the firmware prints "WM\n".
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "WM\n", maxMs: 20000);
        uno.Serial.Text.Should().Contain("WM", "writeto_mem should complete and print WM");
    }

    [Test]
    public void ReadfromMem_CompletesAndEmitsMarker()
    {
        // readfrom_mem sends START, SLA+W, reg, repeated-START, SLA+R, read, STOP.
        // With no device the first SLA+W is NACK'd and the function returns 0,
        // but execution continues and the firmware prints "RM\n".
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "RM\n", maxMs: 20000);
        uno.Serial.Text.Should().Contain("RM", "readfrom_mem should complete and print RM");
    }
}
