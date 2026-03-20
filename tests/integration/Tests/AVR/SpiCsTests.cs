using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;
using AVR8Sharp.Core.Peripherals;

namespace PyMCU.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for examples/avr/spi-cs.
/// Exercises SPI with a custom chip-select pin (cs="PB0").
/// Boot banner: "SCS\n". Sends 0xA5 via SPI, then "D:A5\n" and "OK\n" via UART.
/// The custom CS pin (PB0 = Port B bit 0) must be idle-high after init and
/// driven low during the SPI transfer.
/// </summary>
[TestFixture]
public class SpiCsTests
{
    private string _hex = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _hex = PymcuCompiler.Build("spi-cs");

    [Test]
    public void Boot_SendsBanner()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "SCS\n", maxMs: 100);
        uno.Serial.Text.Should().Contain("SCS");
    }

    [Test]
    public void CsPin_IsHighAfterInit()
    {
        var uno = Sim();
        // Run until the banner is printed (after SPI init, CS should be idle high)
        uno.RunUntilSerial(uno.Serial, "SCS\n", maxMs: 200);
        // PB0 = Port B bit 0 should be HIGH (idle CS)
        uno.PortB.Should().HavePinHigh(0, "CS pin PB0 should be idle-high after SPI init");
    }

    [Test]
    public void Spi_SendsByte_UartReportsCorrectValue()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "D:A5\n", maxMs: 600);
        uno.Serial.Text.Should().Contain("D:A5", "SPI transfer of 0xA5 should be reported");
    }

    [Test]
    public void Spi_TransferCompletes_UartReportsOk()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "OK\n", maxMs: 400);
        uno.Serial.Text.Should().Contain("OK", "firmware should complete SPI transfer and report OK");
    }

    [Test]
    public void CsPin_IsHighAfterTransfer()
    {
        var uno = Sim();
        // Run until OK is printed (after with-block exits, CS should be idle again)
        uno.RunUntilSerial(uno.Serial, "OK\n", maxMs: 400);
        // PB0 = Port B bit 0 should be HIGH (CS deasserted after transfer)
        uno.PortB.Should().HavePinHigh(0, "CS pin PB0 should be idle-high after SPI transfer");
    }

    private ArduinoUnoSimulation Sim()
    {
        var uno = new ArduinoUnoSimulation();
        uno.WithHex(_hex);
        uno.AddSpi(AvrSpi.SpiConfig, out _);
        return uno;
    }
}
