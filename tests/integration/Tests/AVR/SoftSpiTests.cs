using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;

namespace PyMCU.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for examples/avr/softspi.
/// SoftSPI bit-bang SPI Mode 0, MSB-first.
///   SCK  = PC0, MOSI = PC1, MISO = PC2, CS = PC3 (active low).
/// Boot banner: "SSPI\n".
/// Sends 0xA5 via SoftSPI then reports "D:A5\n" and "OK\n" on UART.
/// CS (PC3) is idle-high; driven low during the with-block transfer.
/// </summary>
[TestFixture]
public class SoftSpiTests
{
    private string _hex = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _hex = PymcuCompiler.Build("softspi");

    [Test]
    public void Boot_SendsBanner()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "SSPI\n", maxMs: 200);
        uno.Serial.Should().ContainLine("SSPI");
    }

    [Test]
    public void AfterTransfer_ReportsDataByte()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "D:A5\n", maxMs: 500);
        uno.Serial.Should().ContainLine("D:A5");
    }

    [Test]
    public void AfterTransfer_ReportsOk()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "OK\n", maxMs: 500);
        uno.Serial.Should().ContainLine("OK");
    }

    [Test]
    public void OutputOrder_BannerThenDataThenOk()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "OK\n", maxMs: 500);
        var text = uno.Serial.Text;
        var idxBanner = text.IndexOf("SSPI", StringComparison.Ordinal);
        var idxData   = text.IndexOf("D:A5", StringComparison.Ordinal);
        var idxOk     = text.IndexOf("OK",   StringComparison.Ordinal);
        idxBanner.Should().BeGreaterThanOrEqualTo(0, "boot banner must appear");
        idxData.Should().BeGreaterThan(idxBanner, "data report must follow banner");
        idxOk.Should().BeGreaterThan(idxData, "OK must follow data report");
    }

    private ArduinoUnoSimulation Sim()
    {
        var uno = new ArduinoUnoSimulation();
        uno.WithHex(_hex);
        return uno;
    }
}
