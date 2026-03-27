using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using AVR8Sharp.Core.Peripherals;

namespace PyMCU.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for examples/avr/i2c-rw.
/// Exercises the new I2C.write_to(addr, data) and I2C.read_from(addr) HAL methods.
/// With no I2C device attached the TWI bus operations complete (with NACK),
/// and the firmware outputs "IW\n" after write_to, "IR\n" after read_from.
/// </summary>
[TestFixture]
public class I2cRwTests
{
    private static string _hex = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _hex = PymcuCompiler.BuildFixture("i2c-rw");

    private ArduinoUnoSimulation Sim()
    {
        var uno = new ArduinoUnoSimulation();
        uno.WithHex(_hex);
        uno.AddTwi(AvrTwi.TwiConfig, out _);
        return uno;
    }

    [Test]
    public void WriteTo_CompletesAndEmitsMarker()
    {
        // After i2c.write_to(0x48, 0xAB), firmware sends "IW\n"
        // TWI operations are slow in simulation (each bus transaction ~1 ms simulated time)
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "IW\n", maxMs: 20000);
        uno.Serial.Text.Should().Contain("IW", "write_to should complete and print IW");
    }

    [Test]
    public void ReadFrom_CompletesAndEmitsMarker()
    {
        // After i2c.read_from(0x48), firmware sends "IR\n"
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "IR\n", maxMs: 20000);
        uno.Serial.Text.Should().Contain("IR", "read_from should complete and print IR");
    }
}
