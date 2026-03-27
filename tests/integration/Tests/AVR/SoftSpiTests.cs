using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;
using AVR8Sharp.Core.Peripherals;

namespace PyMCU.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for SoftSPI examples.
///
/// Controller tests (examples/avr/softspi):
///   SCK = PC0, MOSI = PC1, MISO = PC2, CS = PC3 (active low).
///   Boot banner: "SSPI\n".
///   Sends 0xA5 inside a with-block; reports "D:A5\n" and "OK\n" on UART.
///   CS (PC3) is idle-high; driven low during the with-block, high after.
///
/// Peripheral tests (examples/avr/softspi-peripheral):
///   SCK = PC0 (input), MOSI = PC1 (input), MISO = PC2 (output), CS = PC3 (input).
///   Boot banner: "SSPIP\n".
///   Waits for CS low, then exchanges one byte:
///     - Replies with 0xAB on MISO.
///     - Receives controller byte from MOSI.
///   Reports "R:XX\n" and "OK\n" on UART.
/// </summary>
[TestFixture]
public class SoftSpiTests
{
    // ── Controller firmware ──────────────────────────────────────────────────

    private string _hexController = null!;

    [OneTimeSetUp]
    public void BuildFirmware()
    {
        _hexController = PymcuCompiler.Build("softspi");
        _hexPeripheral = PymcuCompiler.Build("softspi-peripheral");
    }

    // ── Controller tests ─────────────────────────────────────────────────────

    [Test]
    public void Controller_Boot_SendsBanner()
    {
        var uno = ControllerSim();
        uno.RunUntilSerial(uno.Serial, "SSPI\n", maxMs: 200);
        uno.Serial.Should().ContainLine("SSPI");
    }

    [Test]
    public void Controller_AfterTransfer_ReportsDataByte()
    {
        var uno = ControllerSim();
        uno.RunUntilSerial(uno.Serial, "D:A5\n", maxMs: 500);
        uno.Serial.Should().ContainLine("D:A5");
    }

    [Test]
    public void Controller_AfterTransfer_ReportsOk()
    {
        var uno = ControllerSim();
        uno.RunUntilSerial(uno.Serial, "OK\n", maxMs: 500);
        uno.Serial.Should().ContainLine("OK");
    }

    [Test]
    public void Controller_OutputOrder_BannerThenDataThenOk()
    {
        var uno = ControllerSim();
        uno.RunUntilSerial(uno.Serial, "OK\n", maxMs: 500);
        var text = uno.Serial.Text;
        var idxBanner = text.IndexOf("SSPI", StringComparison.Ordinal);
        var idxData   = text.IndexOf("D:A5", StringComparison.Ordinal);
        var idxOk     = text.IndexOf("OK",   StringComparison.Ordinal);
        idxBanner.Should().BeGreaterThanOrEqualTo(0, "boot banner must appear");
        idxData.Should().BeGreaterThan(idxBanner, "data report must follow banner");
        idxOk.Should().BeGreaterThan(idxData, "OK must follow data report");
    }

    [Test]
    public void Controller_CsPin_IsHighAfterInit()
    {
        var uno = ControllerSim();
        // CS (PC3) must be idle-high after SoftSPI constructor runs.
        uno.RunUntilSerial(uno.Serial, "SSPI\n", maxMs: 200);
        uno.PortC.Should().HavePinHigh(3, "CS (PC3) must be idle-high after SoftSPI init");
    }

    [Test]
    public void Controller_CsPin_IsHighAfterTransfer()
    {
        var uno = ControllerSim();
        // After the with-block exits, CS must be deasserted (high) again.
        uno.RunUntilSerial(uno.Serial, "OK\n", maxMs: 500);
        uno.PortC.Should().HavePinHigh(3, "CS (PC3) must be idle-high after transfer completes");
    }

    private ArduinoUnoSimulation ControllerSim()
    {
        var uno = new ArduinoUnoSimulation();
        uno.WithHex(_hexController);
        return uno;
    }

    // ── Peripheral firmware ──────────────────────────────────────────────────

    private string _hexPeripheral = null!;

    // ── Peripheral tests ──────────────────────────────────────────────────────

    [Test]
    public void Peripheral_Boot_SendsBanner()
    {
        var uno = PeripheralSim();
        uno.RunUntilSerial(uno.Serial, "SSPIP\n", maxMs: 200);
        uno.Serial.Should().ContainLine("SSPIP");
    }

    [Test]
    public void Peripheral_BeforeCs_MisoIdleLow()
    {
        var uno = PeripheralSim();
        // After boot banner firmware enters cs_asserted() polling loop.
        uno.RunUntilSerial(uno.Serial, "SSPIP\n", maxMs: 200);
        uno.RunMilliseconds(0.1);
        // MISO (PC2) should be idle low before any transfer starts.
        uno.PortC.Should().HavePinLow(2, "MISO (PC2) should be idle-low before CS is asserted");
    }

    [Test]
    public void Peripheral_MisoPredriven_WithReplyMsb()
    {
        var uno = PeripheralSim();
        uno.RunUntilSerial(uno.Serial, "SSPIP\n", maxMs: 200);
        uno.RunMilliseconds(0.1);

        // Assert CS (PC3 low) so firmware exits the cs_asserted() loop and
        // enters exchange(), pre-driving MISO with bit 7 of reply 0xAB = 1.
        uno.PortC.SetPinValue(3, false);   // CS low
        uno.RunMilliseconds(0.2);          // firmware detects CS, enters exchange()

        // Bit 7 of 0xAB = 1; MISO (PC2) should be pre-driven high.
        uno.PortC.Should().HavePinHigh(2, "MISO (PC2) should be pre-driven with MSB of reply 0xAB (bit 7 = 1)");
    }

    [Test]
    public void Peripheral_ReceivesByte_AndReportsOnUart()
    {
        // Simulates an external SPI controller sending 0x5A to the firmware.
        // Firmware replies with 0xAB and must report "R:5A" via UART.
        const byte txByte = 0x5A;   // controller sends this

        var uno = PeripheralSim();
        uno.RunUntilSerial(uno.Serial, "SSPIP\n", maxMs: 200);
        uno.RunMilliseconds(0.1);

        // Assert CS
        uno.PortC.SetPinValue(3, false);  // CS low
        uno.RunMilliseconds(0.2);         // firmware exits CS loop, pre-drives MISO bit 7

        // Clock 8 bits MSB-first (SCK = PC0, MOSI = PC1)
        for (var bit = 7; bit >= 0; bit--)
        {
            var mosiHigh = ((txByte >> bit) & 1) == 1;
            uno.PortC.SetPinValue(1, mosiHigh);  // set MOSI
            uno.PortC.SetPinValue(0, true);       // SCK rising edge
            uno.RunMilliseconds(0.2);             // firmware samples MOSI, waits for falling edge
            uno.PortC.SetPinValue(0, false);      // SCK falling edge
            uno.RunMilliseconds(0.2);             // firmware drives next MISO bit, advances loop
        }

        // Release CS (optional -- firmware exits exchange() after 8 bits)
        uno.PortC.SetPinValue(3, true);

        // Firmware should now report the received byte and OK
        uno.RunUntilSerial(uno.Serial, "OK\n", maxMs: 500);
        uno.Serial.Should().ContainLine("R:5A",
            "firmware must report the byte it received from the controller (0x5A)");
    }

    [Test]
    public void Peripheral_OutputOrder_BannerThenResultThenOk()
    {
        const byte txByte = 0x5A;
        var uno = PeripheralSim();
        uno.RunUntilSerial(uno.Serial, "SSPIP\n", maxMs: 200);
        uno.RunMilliseconds(0.1);
        uno.PortC.SetPinValue(3, false);
        uno.RunMilliseconds(0.2);
        for (var bit = 7; bit >= 0; bit--)
        {
            var mosiHigh = ((txByte >> bit) & 1) == 1;
            uno.PortC.SetPinValue(1, mosiHigh);
            uno.PortC.SetPinValue(0, true);
            uno.RunMilliseconds(0.2);
            uno.PortC.SetPinValue(0, false);
            uno.RunMilliseconds(0.2);
        }
        uno.PortC.SetPinValue(3, true);
        uno.RunUntilSerial(uno.Serial, "OK\n", maxMs: 500);

        var text = uno.Serial.Text;
        var idxBanner = text.IndexOf("SSPIP", StringComparison.Ordinal);
        var idxResult = text.IndexOf("R:",    StringComparison.Ordinal);
        var idxOk     = text.IndexOf("OK",    StringComparison.Ordinal);
        idxBanner.Should().BeGreaterThanOrEqualTo(0, "boot banner must appear");
        idxResult.Should().BeGreaterThan(idxBanner, "result must follow banner");
        idxOk.Should().BeGreaterThan(idxResult, "OK must follow result");
    }

    private ArduinoUnoSimulation PeripheralSim()
    {
        var uno = new ArduinoUnoSimulation();
        uno.WithHex(_hexPeripheral);
        // Pre-drive CS (PC3) high so floating inputs don't immediately assert CS.
        // In the simulator, undriven inputs default to 0 (LOW); CS is active-low,
        // so we must externally hold it HIGH before the firmware starts polling.
        uno.PortC.SetPinValue(3, true);
        return uno;
    }
}
