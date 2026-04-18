using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;

namespace PyMCU.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for examples/avr/multi-pin.
/// 6 LEDs on PB0-PB5; button A on PD2 (advance step), button B on PD3 (reset).
/// Step 0 → PB0 high; step 1 → PB1 high, etc.
/// </summary>
[TestFixture]
public class MultiPinTests
{
    private SimSession _session = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _session = new SimSession(PymcuCompiler.Build("multi-pin"));

    [Test]
    public void Initial_Step0_PB0IsHigh()
    {
        var uno = Sim();
        uno.RunMilliseconds(30); // a few iterations
        uno.PortB.Should().HavePinHigh(0);
        uno.PortB.Should().HavePinLow(1);
    }

    [Test]
    public void ButtonAPress_AdvancesToStep1()
    {
        var uno = Sim();
        uno.RunMilliseconds(30);
        PressA(uno);
        uno.RunMilliseconds(50);
        uno.PortB.Should().HavePinLow(0);
        uno.PortB.Should().HavePinHigh(1);
    }

    [Test]
    public void ButtonBPress_ResetsToStep0()
    {
        var uno = Sim();
        uno.RunMilliseconds(30);
        PressA(uno); // step = 1
        uno.RunMilliseconds(40);
        PressB(uno); // reset to 0
        uno.RunMilliseconds(40);
        uno.PortB.Should().HavePinHigh(0);
        uno.PortB.Should().HavePinLow(1);
    }

    [Test]
    public void SixPresses_WrapsToStep0()
    {
        var uno = Sim();
        uno.RunMilliseconds(30);
        for (var i = 0; i < 6; i++)
        {
            PressA(uno);
            uno.RunMilliseconds(40);
        }
        uno.PortB.Should().HavePinHigh(0); // wrapped around
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void PressA(ArduinoUnoSimulation uno)
    {
        uno.PortD.SetPinValue(2, false);
        uno.RunMilliseconds(25);
        uno.PortD.SetPinValue(2, true);
    }

    private static void PressB(ArduinoUnoSimulation uno)
    {
        uno.PortD.SetPinValue(3, false);
        uno.RunMilliseconds(25);
        uno.PortD.SetPinValue(3, true);
    }

    private ArduinoUnoSimulation Sim()
    {
        var uno = _session.Reset();
        uno.PortD.SetPinValue(2, true); // A released
        uno.PortD.SetPinValue(3, true); // B released
        return uno;
    }
}
