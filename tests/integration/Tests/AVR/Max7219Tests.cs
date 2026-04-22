using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;
using AVR8Sharp.Core.Peripherals;

namespace PyMCU.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for examples/avr/max7219.
/// MAX7219 8x8 LED matrix driver via hardware SPI.
/// Hardware: MOSI=PB3, SCK=PB5, CS=PB2 (active-low chip select).
///
/// Expected UART output:
///   "MAX7219\n"   -- boot banner
///   "OK\n"        -- init + clear complete
/// After init, the firmware writes a checkerboard pattern and sets brightness.
/// </summary>
[TestFixture]
public class Max7219Tests
{
    private string _hex = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _hex = PymcuCompiler.Build("max7219");

    [Test]
    public void Boot_SendsBanner()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "MAX7219\n", maxMs: 300);
        uno.Serial.Should().ContainLine("MAX7219");
    }

    [Test]
    public void Init_CompletesAndEmitsOk()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "OK\n", maxMs: 500);
        uno.Serial.Text.Should().Contain("OK",
            "MAX7219 init and clear should complete without error");
    }

    [Test]
    public void CsPin_ConfiguredAsOutput()
    {
        // CS = PB2 (bit 2); DDRB bit 2 must be set as output.
        const int DDRB = 0x24;
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "MAX7219\n", maxMs: 300);
        (uno.Data[DDRB] & 0x04).Should().Be(0x04,
            "DDRB bit 2 (PB2, CS) must be configured as output");
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
