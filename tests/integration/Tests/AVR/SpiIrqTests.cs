using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;
using AVR8Sharp.Core.Peripherals;

namespace PyMCU.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for examples/avr/spi-irq.
/// Hardware SPI in peripheral (slave) mode with interrupt-driven byte receive.
/// ISR reads SPDR on each SPI STC interrupt and stores to GPIOR0[0];
/// main loop polls GPIOR0[0] and prints the byte in hex over UART.
///
/// Expected UART output:
///   "SPII\n"   -- boot banner (SPI Interrupt)
///   "XX\n"     -- two hex digits for each byte received over SPI
///
/// The AVR8Sharp simulator attaches an SPI peripheral probe so we can
/// stimulate the SPI STC interrupt by writing to the SPDR data register.
/// </summary>
[TestFixture]
public class SpiIrqTests
{
    private string _hex = null!;

    // ATmega328P hardware SPI data register address (data-space)
    private const int SPDR_ADDR = 0x4E;

    [OneTimeSetUp]
    public void BuildFirmware() => _hex = PymcuCompiler.Build("spi-irq");

    [Test]
    public void Boot_SendsBanner()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "SPII\n", maxMs: 200);
        uno.Serial.Should().ContainLine("SPII");
    }

    [Test]
    public void SpiPeripheral_Configured_MisoIsOutput()
    {
        // In SPI peripheral mode MISO (PB4) is the only output.
        // DDRB bit 4 must be 1; SCK (PB5), MOSI (PB3), SS (PB2) must be inputs.
        const int DDRB = 0x24;
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "SPII\n", maxMs: 200);
        (uno.Data[DDRB] & 0x10).Should().Be(0x10, "DDRB bit 4 (MISO/PB4) must be output in SPI peripheral mode");
        (uno.Data[DDRB] & 0x20).Should().Be(0x00, "DDRB bit 5 (SCK/PB5) must be input in SPI peripheral mode");
        (uno.Data[DDRB] & 0x08).Should().Be(0x00, "DDRB bit 3 (MOSI/PB3) must be input in SPI peripheral mode");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private ArduinoUnoSimulation Sim()
    {
        var uno = new ArduinoUnoSimulation();
        uno.WithHex(_hex);
        uno.AddSpi(AvrSpi.SpiConfig, out _);
        return uno;
    }
}
