using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;

namespace PyMCU.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for examples/avr/state-machine.
/// Traffic light FSM: RED(3s) → RED+YEL(1s) → GREEN(3s) → YELLOW(1s) → RED...
/// Outputs: PB0=red, PB1=yellow, PB2=green; UART logs state name.
/// Timer0 at prescaler 256: overflow every ~4.096 ms; 244 OVFs ≈ 1 s.
/// </summary>
[TestFixture]
public class StateMachineTests
{
    private string _hex = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _hex = PymcuCompiler.Build("state-machine");

    [Test]
    public void Initial_RedLedIsOn()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "RED");
        uno.PortB.Should().HavePinHigh(0); // PB0 = red
        uno.PortB.Should().HavePinLow(1);  // PB1 = yellow (off)
        uno.PortB.Should().HavePinLow(2);  // PB2 = green (off)
    }

    [Test]
    public void Initial_UartPrintsRed()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "RED");
        uno.Serial.Should().ContainLine("RED");
    }

    [Test]
    public void After3Seconds_TransitionsToRedYellow()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "RED");
        // RED phase is 732 overflows * 4.096 ms ≈ 3 s; add margin
        uno.RunUntilSerial(uno.Serial, "RED+YEL", maxMs: 4000);
        uno.Serial.Should().ContainLine("RED+YEL");
        uno.PortB.Should().HavePinHigh(0); // red still on
        uno.PortB.Should().HavePinHigh(1); // yellow also on
        uno.PortB.Should().HavePinLow(2);
    }

    [Test]
    public void After4Seconds_TransitionsToGreen()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "GREEN", maxMs: 5000);
        uno.Serial.Should().ContainLine("GREEN");
        uno.PortB.Should().HavePinLow(0);
        uno.PortB.Should().HavePinLow(1);
        uno.PortB.Should().HavePinHigh(2);
    }

    [Test]
    public void After7Seconds_TransitionsToYellow()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "YELLOW", maxMs: 8500);
        uno.Serial.Should().ContainLine("YELLOW");
        uno.PortB.Should().HavePinLow(0);
        uno.PortB.Should().HavePinHigh(1);
        uno.PortB.Should().HavePinLow(2);
    }

    [Test]
    public void After8Seconds_CyclesBackToRed()
    {
        var uno = Sim();
        // Skip first RED, wait for the cycle to complete and RED appear again
        uno.RunUntilSerial(uno.Serial, "RED\n");
        uno.Serial.Clear();
        uno.RunUntilSerial(uno.Serial, "RED\n", maxMs: 9000);
        uno.Serial.Should().ContainLine("RED");
    }

    private ArduinoUnoSimulation Sim()
    {
        var uno = new ArduinoUnoSimulation();
        uno.WithHex(_hex);
        return uno;
    }
}
