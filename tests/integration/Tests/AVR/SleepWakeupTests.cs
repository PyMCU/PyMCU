using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;

namespace Whisnake.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for examples/avr/sleep-wakeup.
/// Tests sleep_idle() from whisnake.hal.power.
/// The firmware sleeps (idle mode), wakes on INT0 (PD2 falling edge), prints "WAKE".
/// After 5 wakes it prints "DONE".
/// </summary>
[TestFixture]
public class SleepWakeupTests
{
    private string _hex = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _hex = PymcuCompiler.Build("sleep-wakeup");

    [Test]
    public void Boot_PrintsSleepDemo()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "SLEEP DEMO", maxMs: 200);
        uno.Serial.Should().Contain("SLEEP DEMO");
    }

    [Test]
    public void AfterInterrupt_PrintsWake()
    {
        var uno = Sim();
        // Wait for "SLEEP" to appear (firmware is now sleeping)
        uno.RunUntilSerial(uno.Serial, "SLEEP\n", maxMs: 200);

        // Simulate a falling edge on PD2 (INT0) to wake the MCU
        uno.PortD.SetPinValue(2, true);
        uno.RunMilliseconds(1);
        uno.PortD.SetPinValue(2, false); // falling edge
        uno.RunMilliseconds(1);
        uno.PortD.SetPinValue(2, true);  // restore

        uno.RunUntilSerial(uno.Serial, "WAKE", maxMs: 200);
        uno.Serial.Should().Contain("WAKE");
    }

    [Test]
    public void After5Interrupts_PrintsDone()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "SLEEP\n", maxMs: 200);

        // Fire 5 falling edges on INT0 (PD2) to wake the firmware 5 times
        for (int i = 0; i < 5; i++)
        {
            uno.PortD.SetPinValue(2, true);
            uno.RunMilliseconds(1);
            uno.PortD.SetPinValue(2, false);
            uno.RunMilliseconds(2);
            uno.PortD.SetPinValue(2, true);
            int expectedWakes = i + 1;
            uno.RunUntilSerial(uno.Serial, s => s.Split('\n').Count(l => l.Contains("WAKE")) >= expectedWakes,
                maxMs: 300);
        }

        uno.RunUntilSerial(uno.Serial, "DONE", maxMs: 200);
        uno.Serial.Should().Contain("DONE");
    }

    private ArduinoUnoSimulation Sim()
    {
        var uno = new ArduinoUnoSimulation();
        uno.WithHex(_hex);
        return uno;
    }
}
