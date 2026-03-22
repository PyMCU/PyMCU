using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;
using AVR8Sharp.Core.Peripherals;

namespace Whipsnake.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for examples/avr/adc-read.
/// Boot banner: "ADC\n" (bytes 65,68,67,10). Then polls ADCSRA[6] for conversion.
/// </summary>
[TestFixture]
public class AdcReadTests
{
    private string _hex = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _hex = PymcuCompiler.Build("adc-read");

    [Test]
    public void Boot_SendsAdcBanner()
    {
        var uno = Sim();
        // "ADC\n" encoded as individual uart.write() calls
        uno.RunUntilSerialBytes(uno.Serial, 4, maxMs: 100);
        uno.Serial.Should().HaveBytesAt(0, [65, 68, 67, 10]); // A D C \n
    }

    [Test]
    public void Boot_AdcBannerContainsADC()
    {
        var uno = Sim();
        uno.RunUntilSerialBytes(uno.Serial, 4, maxMs: 100);
        uno.Serial.Should().Contain("ADC");
    }

    [Test]
    public void AfterBoot_ReceivesConversionResults()
    {
        var uno = Sim();
        // Add ADC peripheral so conversions complete
        uno.AddAdc(AvrAdc.AdcConfig, out _);
        uno.RunUntilSerialBytes(uno.Serial, 6, maxMs: 500); // banner(4) + 2 results
        // Results are 0 or 1 (bit 0 of ADCL); just check we got some
        uno.Serial.ByteCount.Should().BeGreaterThan(4);
    }

    private ArduinoUnoSimulation Sim()
    {
        var uno = new ArduinoUnoSimulation();
        uno.WithHex(_hex);
        return uno;
    }
}
