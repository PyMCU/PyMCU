using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;
using AVR8Sharp.Core.Peripherals;

namespace PyMCU.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for examples/avr/i2c-irq.
/// I2C (TWI) peripheral at address 0x42 with interrupt-driven byte receive.
/// ISR handles SLA+W, data, and STOP TWI states; stores each received data
/// byte in GPIOR0[0] for the main loop to print as hex over UART.
///
/// Expected UART output:
///   "I2CI\n"   -- boot banner (I2C Interrupt)
///   "XX\n"     -- two hex digits for each data byte received from controller
///
/// Because AVR8Sharp's TWI simulation does not inject arbitrary controller
/// transactions, these tests only verify the boot phase and peripheral
/// configuration (TWEN + TWEA bits set, global interrupts enabled).
/// </summary>
[TestFixture]
public class I2cIrqTests
{
    private string _hex = null!;

    // ATmega328P TWI control register (data-space address)
    private const int TWCR_ADDR = 0xBC;

    // TWCR bit masks
    private const byte TWEN  = 0x04; // bit 2: TWI enable
    private const byte TWEA  = 0x40; // bit 6: TWI enable acknowledge
    private const byte TWIE  = 0x01; // bit 0: TWI interrupt enable

    [OneTimeSetUp]
    public void BuildFirmware() => _hex = PymcuCompiler.Build("i2c-irq");

    [Test]
    public void Boot_SendsBanner()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "I2CI\n", maxMs: 300);
        uno.Serial.Should().ContainLine("I2CI");
    }

    [Test]
    public void Twi_EnabledAndAcknowledgeSet_AfterInit()
    {
        // i2c.irq() must enable TWEN and TWEA so the peripheral is ready to ACK.
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "I2CI\n", maxMs: 300);
        (uno.Data[TWCR_ADDR] & TWEN).Should().Be(TWEN,
            "TWEN must be set after I2C peripheral init");
        (uno.Data[TWCR_ADDR] & TWEA).Should().Be(TWEA,
            "TWEA must be set so the peripheral ACKs its own address");
    }

    [Test]
    public void Twi_InterruptEnabled_AfterInit()
    {
        // i2c.irq() must enable the TWI interrupt (TWIE) so the ISR fires.
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "I2CI\n", maxMs: 300);
        (uno.Data[TWCR_ADDR] & TWIE).Should().Be(TWIE,
            "TWIE must be set after i2c.irq() so the TWI ISR is enabled");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private ArduinoUnoSimulation Sim()
    {
        var uno = new ArduinoUnoSimulation();
        uno.WithHex(_hex);
        uno.AddTwi(AvrTwi.TwiConfig, out _);
        return uno;
    }
}
