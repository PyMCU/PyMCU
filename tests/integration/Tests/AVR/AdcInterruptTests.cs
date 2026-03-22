using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;
using AVR8Sharp.Core.Peripherals;

namespace Whipsnake.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for examples/avr/adc-interrupt.
/// AnalogPin.start_conversion() sets ADIE=1 then ADSC=1.
/// ADC complete ISR fires at vector byte 0x002A / word 0x0015 on ATmega328P.
/// ISR stores ADCL in GPIOR1 and signals GPIOR0[1]; main loop sends the byte.
/// </summary>
[TestFixture]
public class AdcInterruptTests
{
    private string _hex = null!;

    // ATmega328P memory-mapped register addresses
    private const int ADCSRA = 0x7A;
    private const int ADMUX  = 0x7C;

    [OneTimeSetUp]
    public void BuildFirmware() => _hex = PymcuCompiler.Build("adc-interrupt");

    [Test]
    public void Boot_SendsBanner()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "ADC IRQ");
        uno.Serial.Should().ContainLine("ADC IRQ");
    }

    [Test]
    public void Init_AdcEnabled_And_Prescaler128()
    {
        // ADCSRA = 0x87: ADEN(bit7)=1, ADPS[2:0]=111 (prescaler 128)
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "ADC IRQ");
        uno.RunMilliseconds(5);
        var adcsra = uno.Data[ADCSRA];
        (adcsra & 0x80).Should().Be(0x80, "ADEN (bit 7) must be set to enable ADC");
        (adcsra & 0x07).Should().Be(0x07, "ADPS[2:0]=111 selects prescaler 128");
    }

    [Test]
    public void Init_Admux_Channel0_Avcc()
    {
        // ADMUX = 0x40: REFS1:0=01 (AVCC ref), MUX3:0=0000 (ADC0/PC0)
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "ADC IRQ");
        uno.RunMilliseconds(5);
        var admux = uno.Data[ADMUX];
        (admux & 0xC0).Should().Be(0x40, "REFS1:0=01 selects AVCC reference");
        (admux & 0x0F).Should().Be(0x00, "MUX3:0=0000 selects ADC0 channel");
    }

    [Test]
    public void Init_Adie_Set_After_StartConversion()
    {
        // ADIE = bit 3 of ADCSRA must be set before ADSC
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "ADC IRQ");
        uno.RunMilliseconds(5);
        var adcsra = uno.Data[ADCSRA];
        (adcsra & 0x08).Should().Be(0x08, "ADIE (bit 3 of ADCSRA) must be set for interrupt-driven mode");
    }

    [Test]
    public void AfterConversion_ReceivesResults()
    {
        // With ADC peripheral attached, conversions complete and ISR fires.
        var uno = Sim();
        uno.AddAdc(AvrAdc.AdcConfig, out _);
        uno.RunUntilSerial(uno.Serial, "ADC IRQ");
        // Wait for at least one result byte + '\n' after the banner.
        var before = uno.Serial.ByteCount;
        uno.RunUntilSerialBytes(uno.Serial, before + 2, maxMs: 500);
        uno.Serial.ByteCount.Should().BeGreaterThan(before, "ADC ISR must produce result bytes after conversion");
    }

    private ArduinoUnoSimulation Sim()
    {
        var uno = new ArduinoUnoSimulation();
        uno.WithHex(_hex);
        return uno;
    }
}
