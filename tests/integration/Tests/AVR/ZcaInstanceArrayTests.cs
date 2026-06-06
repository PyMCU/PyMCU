using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;

namespace PyMCU.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for fixtures/avr/zca-instance-array.
///
/// Submodule-free end-to-end regression for the compile-time ZCA-instance-array
/// machinery (list-comp construction, for-in nested-method calls, enumerate driving
/// a property setter with a runtime value). Mirrors compat-cp-gpio but with a plain
/// pymcu class, so it stays green even if the CircuitPython compat package changes.
///
///   leds = [Led(p) for p in ("PD5", "PD6", "PD7")]
///   for led in leds: led.off()
///   for bit, led in enumerate(leds): led.level = (pattern >> bit) and 1   # pattern=1
///
/// pattern=1 -> PD5=HIGH (bit0), PD6=LOW (bit1), PD7=LOW (bit2); DDRD 5-7 outputs.
/// </summary>
[TestFixture]
public class ZcaInstanceArrayTests
{
    private SimSession _session = null!;

    [OneTimeSetUp]
    public void BuildFirmware() =>
        _session = new SimSession(PymcuCompiler.BuildFixture("zca-instance-array"));

    private ArduinoUnoSimulation Sim() => _session.Reset();

    [Test]
    public void Boot_SendsBanner()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "ZA\n", maxMs: 200);
        uno.Serial.Text.Should().Contain("ZA");
    }

    [Test]
    public void CompletesSetup_SendsDone()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "DONE\n", maxMs: 200);
        uno.Serial.Text.Should().Contain("DONE");
    }

    [Test]
    public void D5_IsHigh_FromEnumeratePropertySetter()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "DONE\n", maxMs: 200);
        uno.PortD.Should().HavePinHigh(5,
            "bit0 of pattern=1 sets PD5 HIGH via a property setter driven by enumerate over the ZCA array");
    }

    [Test]
    public void D6_IsLow_FromEnumeratePropertySetter()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "DONE\n", maxMs: 200);
        uno.PortD.Should().HavePinLow(6,
            "bit1 of pattern=1 is 0 -> PD6 LOW");
    }

    [Test]
    public void D7_IsLow_FromEnumeratePropertySetter()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "DONE\n", maxMs: 200);
        uno.PortD.Should().HavePinLow(7,
            "bit2 of pattern=1 is 0 -> PD7 LOW");
    }
}
