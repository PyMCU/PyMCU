using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;

namespace PyMCU.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for fixtures/avr/adc-temp.
/// Verifies that AnalogPin("TEMP") loads ADMUX with 0xC8
/// (REFS1:0 = 11 for internal 1.1V reference, MUX = 1000 for ADC channel 8).
///
/// The fixture stores ADMUX in GPIOR0 (0x3E) then executes BREAK.
/// </summary>
[TestFixture]
public class AdcTempTests
{
    private const int Gpior0Addr = 0x3E;

    private SimSession _session = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _session = new SimSession(PymcuCompiler.BuildFixture("adc-temp"));

    [Test]
    public void AdcTemp_SetsAdmuxTo0xC8()
    {
        var uno = Sim();
        uno.RunToBreak();
        uno.Data[Gpior0Addr].Should().Be(0xC8,
            "ADMUX must select 1.1V internal reference (REFS1:0=11) and ADC channel 8 (MUX=1000)");
    }

    private ArduinoUnoSimulation Sim() => _session.Reset();
}
