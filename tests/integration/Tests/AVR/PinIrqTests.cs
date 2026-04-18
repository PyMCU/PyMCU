using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;

namespace PyMCU.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for examples/avr/pin-irq.
/// Pin.irq(Pin.IRQ_FALLING, handler) configures INT0 on PD2 (falling edge).
/// ISR sets GPIOR0[0]; main loop detects it, increments count, sends raw byte.
/// Boot banner: "PIN IRQ\n".
/// </summary>
[TestFixture]
public class PinIrqTests
{
    private SimSession _session = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _session = new SimSession(PymcuCompiler.Build("pin-irq"));

    [Test]
    public void Boot_SendsBanner()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "PIN IRQ\n", maxMs: 200);
        uno.Serial.Should().ContainLine("PIN IRQ");
    }

    [Test]
    public void Interrupt_IncreasesCounter()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "PIN IRQ\n", maxMs: 200);
        var before = uno.Serial.ByteCount;

        // Falling edge on PD2 triggers INT0
        uno.PortD.SetPinValue(2, true);
        uno.RunMilliseconds(1);
        uno.PortD.SetPinValue(2, false);
        uno.RunMilliseconds(20);

        uno.Serial.ByteCount.Should().BeGreaterThan(before, "count byte should be sent after interrupt");
        uno.Serial.Bytes.Skip(before).First().Should().Be(1, "first count should be 1");
    }

    [Test]
    public void MultipleInterrupts_CountsCorrectly()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "PIN IRQ\n", maxMs: 200);
        var before = uno.Serial.ByteCount;

        for (var i = 0; i < 3; i++)
        {
            uno.PortD.SetPinValue(2, true);
            uno.RunMilliseconds(2);
            uno.PortD.SetPinValue(2, false);
            uno.RunMilliseconds(20);
        }

        var counts = uno.Serial.Bytes.Skip(before).Take(3).ToArray();
        counts.Should().Equal([1, 2, 3], "each interrupt increments the counter");
    }

    private ArduinoUnoSimulation Sim()
    {
        var uno = _session.Reset();
        uno.PortD.SetPinValue(2, true); // button released initially (PD2 high)
        return uno;
    }
}
