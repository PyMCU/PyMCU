using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;
using AVR8Sharp.Core.Peripherals;

namespace Whisnake.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for examples/avr/soft-pwm.
/// Timer0 OVF ISR sets GPIOR0[0]; main loop drives a 0-99 PWM counter
/// and steps the duty cycle from 0→25→50→75→100→75→50→25→0 (bounce).
/// Tests: uint8 >= comparisons, PORTB direct write, helper function with
///        multiple return paths, duty-cycle state machine.
/// </summary>
[TestFixture]
public class SoftPwmTests
{
    private string _hex = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _hex = PymcuCompiler.Build("soft-pwm");

    [Test]
    public void Boot_SendsBanner()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "SOFT PWM");
        uno.Serial.Should().ContainLine("SOFT PWM");
    }

    [Test]
    public void FirstDutyStep_Sends25()
    {
        // After 100 timer ticks (~1.6s) the duty advances from 0→25 and sends 25+'\n'.
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "SOFT PWM\n");
        var before = uno.Serial.ByteCount;

        uno.RunUntilSerialBytes(uno.Serial, before + 2, maxMs: 2000);

        uno.Serial.Bytes[before].Should().Be(25, "first duty step = 25");
        uno.Serial.Bytes[before + 1].Should().Be((byte)'\n');
    }

    [Test]
    public void SecondDutyStep_Sends50()
    {
        // Two steps: 0→25→50
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "SOFT PWM\n");
        var before = uno.Serial.ByteCount;

        uno.RunUntilSerialBytes(uno.Serial, before + 4, maxMs: 4000);

        uno.Serial.Bytes[before].Should().Be(25,    "step 1 duty = 25");
        uno.Serial.Bytes[before + 2].Should().Be(50, "step 2 duty = 50");
    }

    [Test]
    public void FirstFiveSteps_CorrectSequence()
    {
        // Steps: 25, 50, 75, 100, 75 (5 steps × ~1.6s ≈ 8s — use sim time)
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "SOFT PWM\n");
        var before = uno.Serial.ByteCount;

        uno.RunUntilSerialBytes(uno.Serial, before + 10, maxMs: 10000);

        var duties = new byte[] { 25, 50, 75, 100, 75 };
        for (var i = 0; i < 5; i++)
        {
            uno.Serial.Bytes[before + i * 2].Should().Be(duties[i], $"step {i + 1} duty");
            uno.Serial.Bytes[before + i * 2 + 1].Should().Be((byte)'\n');
        }
    }

    private ArduinoUnoSimulation Sim()
    {
        var uno = new ArduinoUnoSimulation();
        uno.WithHex(_hex);
        return uno;
    }
}
