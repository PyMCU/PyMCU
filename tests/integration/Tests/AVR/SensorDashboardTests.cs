using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;
using AVR8Sharp.Core.Peripherals;

namespace Whisnake.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for examples/avr/sensor-dashboard.
/// Timer0_OVF ISR (prescaler 256, 64 ticks ~262ms) drives periodic ADC0 sampling.
/// INT0 falling edge on PD2 toggles verbose/compact display mode.
/// Verbose format:  "R:HH A:HH L:HH H:HH\n"
/// Compact format:  "HH\n"
/// </summary>
[TestFixture]
public class SensorDashboardTests
{
    private string _hex = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _hex = PymcuCompiler.Build("sensor-dashboard");

    [Test]
    public void Boot_SendsBanner()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "SENSOR DASHBOARD");
        uno.Serial.Should().ContainLine("SENSOR DASHBOARD");
    }

    [Test]
    public void VerboseFrame_ContainsRawAndAvg_Labels()
    {
        // After boot, Timer0 fires every ~4ms; after 64 ticks (~262ms) the
        // first ADC sample is taken and a verbose frame is emitted.
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "SENSOR DASHBOARD\n");

        // Wait up to 1000ms sim-time for a complete verbose frame (all four labels)
        uno.RunUntilSerial(uno.Serial,
            s => s.Contains("R:") && s.Contains("A:") && s.Contains("L:") && s.Contains("H:"),
            maxMs: 1000);

        uno.Serial.Text.Should().Contain("R:", "verbose frame must include raw label");
        uno.Serial.Text.Should().Contain("A:", "verbose frame must include avg label");
        uno.Serial.Text.Should().Contain("L:", "verbose frame must include min label");
        uno.Serial.Text.Should().Contain("H:", "verbose frame must include max label");
    }

    [Test]
    public void Int0FallingEdge_TogglesCompactMode()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "SENSOR DASHBOARD\n");

        // Wait for at least one complete verbose frame so we know the loop is running
        uno.RunUntilSerial(uno.Serial,
            s => s.Contains("R:") && s.Contains("A:") && s.Contains("H:"),
            maxMs: 1000);

        // Simulate INT0 falling edge (button press on PD2)
        uno.PortD.SetPinValue(2, true);
        uno.RunMilliseconds(1);
        uno.PortD.SetPinValue(2, false);

        // Wait for the mode-change message
        uno.RunUntilSerial(uno.Serial, s => s.Contains("MODE:COMPACT"), maxMs: 500);
        uno.Serial.Text.Should().Contain("MODE:COMPACT", "INT0 press should toggle to compact mode");
    }

    [Test]
    public void AfterCompactToggle_FramesAreCompact()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "SENSOR DASHBOARD\n");

        // Wait for first complete verbose frame
        uno.RunUntilSerial(uno.Serial,
            s => s.Contains("R:") && s.Contains("A:") && s.Contains("H:"),
            maxMs: 1000);

        // Toggle to compact via INT0
        uno.PortD.SetPinValue(2, true);
        uno.RunMilliseconds(1);
        uno.PortD.SetPinValue(2, false);
        uno.RunUntilSerial(uno.Serial, s => s.Contains("MODE:COMPACT"), maxMs: 500);

        var beforeCompact = uno.Serial.ByteCount;

        // Wait for at least one compact frame (~262ms)
        uno.RunMilliseconds(500);

        // Any new output after MODE:COMPACT should NOT start with "R:"
        var newText = uno.Serial.Text[beforeCompact..];
        // Compact frames are "HH\n" — they must not contain "R:" or "A:" labels
        newText.Should().NotContain("R:", "compact mode must not emit verbose labels");
    }

    [Test]
    public void SecondInt0Press_RestoresVerboseMode()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "SENSOR DASHBOARD\n");

        // First INT0: verbose -> compact
        uno.PortD.SetPinValue(2, true); uno.RunMilliseconds(1);
        uno.PortD.SetPinValue(2, false);
        uno.RunUntilSerial(uno.Serial, s => s.Contains("MODE:COMPACT"), maxMs: 1000);

        // Second INT0: compact -> verbose
        uno.PortD.SetPinValue(2, true); uno.RunMilliseconds(1);
        uno.PortD.SetPinValue(2, false);
        uno.RunUntilSerial(uno.Serial, s => s.Contains("MODE:VERBOSE"), maxMs: 1000);

        uno.Serial.Text.Should().Contain("MODE:VERBOSE", "second INT0 press should restore verbose mode");

        // After MODE:VERBOSE, the next frame should have "R:" again
        uno.RunUntilSerial(uno.Serial,
            s => s.LastIndexOf("R:", StringComparison.Ordinal) >
                 s.IndexOf("MODE:VERBOSE", StringComparison.Ordinal),
            maxMs: 1000);
        uno.Serial.Text.Should().Contain("R:");
    }

    private ArduinoUnoSimulation Sim()
    {
        var uno = new ArduinoUnoSimulation();
        uno.WithHex(_hex);
        uno.AddAdc(AvrAdc.AdcConfig, out _);
        uno.PortD.SetPinValue(2, true); // INT0 button released (active-low)
        return uno;
    }
}
