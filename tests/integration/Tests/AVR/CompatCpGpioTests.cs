using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;

namespace PyMCU.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for fixtures/avr/compat-cp-gpio.
/// Verifies CircuitPython-style digitalio list comprehension on AVR:
///   outs = [digitalio.DigitalInOut(p) for p in (board.D5, board.D6, board.D7)]
///   for pin in outs: pin.direction = Direction.OUTPUT
///   for bit, pin in enumerate(outs): pin.value = (pattern >> bit) and 1
///
/// pattern=1 → PD5=HIGH (bit0), PD6=LOW (bit1), PD7=LOW (bit2)
/// </summary>
[TestFixture]
public class CompatCpGpioTests
{
    private SimSession _session = null!;

    [OneTimeSetUp]
    public void BuildFirmware() =>
        _session = new SimSession(PymcuCompiler.BuildFixture("compat-cp-gpio"));

    [Test]
    public void D5_IsHighAfterSetup()
    {
        var uno = Sim();
        uno.RunMilliseconds(1);
        uno.PortD.Should().HavePinHigh(5,
            "bit0 of pattern=1 sets PD5 HIGH via enumerate over list comp");
    }

    [Test]
    public void D6_IsLowAfterSetup()
    {
        var uno = Sim();
        uno.RunMilliseconds(1);
        uno.PortD.Should().HavePinLow(6,
            "bit1 of pattern=1 sets PD6 LOW via enumerate over list comp");
    }

    [Test]
    public void D7_IsLowAfterSetup()
    {
        var uno = Sim();
        uno.RunMilliseconds(1);
        uno.PortD.Should().HavePinLow(7,
            "bit2 of pattern=1 sets PD7 LOW via enumerate over list comp");
    }

    private ArduinoUnoSimulation Sim() => _session.Reset();
}
