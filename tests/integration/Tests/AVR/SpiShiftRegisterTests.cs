using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;
using AVR8Sharp.Core.Peripherals;

namespace PyMCU.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for examples/avr/spi-shift-register.
/// Hardware SPI to 74HC595. Boot: "SPI 74HC595 DEMO\n".
/// Sends pattern byte to UART after each frame.
/// </summary>
[TestFixture]
public class SpiShiftRegisterTests
{
    private string _hex = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _hex = PymcuCompiler.Build("spi-shift-register");

    [Test]
    public void Boot_SendsDemoBanner()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "SPI 74HC595 DEMO");
        uno.Serial.Should().ContainLine("SPI 74HC595 DEMO");
    }

    [Test]
    public void FirstPattern_Is0x01()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "SPI 74HC595 DEMO\n");
        var before = uno.Serial.ByteCount;
        uno.RunUntilSerialBytes(uno.Serial, before + 1, maxMs: 500);
        uno.Serial.Bytes[before].Should().Be(0x01, "running light starts at bit 0");
    }

    [Test]
    public void SecondPattern_Is0x02()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "SPI 74HC595 DEMO\n");
        var before = uno.Serial.ByteCount;
        uno.RunUntilSerialBytes(uno.Serial, before + 2, maxMs: 1000);
        uno.Serial.Bytes[before + 1].Should().Be(0x02, "rotate left");
    }

    private ArduinoUnoSimulation Sim()
    {
        var uno = new ArduinoUnoSimulation();
        uno.WithHex(_hex);
        uno.AddSpi(AvrSpi.SpiConfig, out _);
        return uno;
    }
}
