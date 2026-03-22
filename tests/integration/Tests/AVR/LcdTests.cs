using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;

namespace Whipsnake.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for examples/avr/lcd.
/// HD44780 LCD driver in 4-bit mode.
/// Pins: RS->PD4, EN->PD5, D4->PD6, D5->PD7, D6->PB0, D7->PB1.
/// UART output: "LCD\n" (boot), "OK\n" (after lcd.init()).
/// </summary>
[TestFixture]
public class LcdTests
{
    private string _hex = null!;

    // ATmega328P DDR register data-space addresses
    private const int DDRD = 0x2A;  // Port D direction: PD4=bit4, PD5=bit5, PD6=bit6, PD7=bit7
    private const int DDRB = 0x24;  // Port B direction: PB0=bit0, PB1=bit1

    [OneTimeSetUp]
    public void BuildFirmware() => _hex = PymcuCompiler.Build("lcd");

    [Test]
    public void Boot_SendsBanner()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "LCD\n", maxMs: 100);
        uno.Serial.Text.Should().Contain("LCD");
    }

    [Test]
    public void Init_SendsOk()
    {
        var uno = Sim();
        // lcd.init() takes ~50ms (power-on wait) plus 4-bit sequence delays
        uno.RunUntilSerial(uno.Serial, "OK\n", maxMs: 200);
        uno.Serial.Text.Should().Contain("OK");
    }

    [Test]
    public void Init_PortD_Pins_ConfiguredAsOutput()
    {
        // RS(PD4), EN(PD5), D4(PD6), D5(PD7) must all be outputs after lcd.init()
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "OK\n", maxMs: 200);
        var ddrd = uno.Data[DDRD];
        (ddrd & 0xF0).Should().Be(0xF0, "DDRD bits 4-7 (PD4=RS, PD5=EN, PD6=D4, PD7=D5) must be set as outputs");
    }

    [Test]
    public void Init_PortB_Pins_ConfiguredAsOutput()
    {
        // D6(PB0), D7(PB1) must be outputs after lcd.init()
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "OK\n", maxMs: 200);
        var ddrb = uno.Data[DDRB];
        (ddrb & 0x03).Should().Be(0x03, "DDRB bits 0-1 (PB0=D6, PB1=D7) must be set as outputs");
    }

    private ArduinoUnoSimulation Sim()
    {
        var uno = new ArduinoUnoSimulation();
        uno.WithHex(_hex);
        return uno;
    }
}
