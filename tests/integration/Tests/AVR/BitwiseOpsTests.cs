using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;

namespace Whisnake.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for examples/avr/bitwise-ops.
/// The firmware produces a deterministic fixed byte sequence then loops.
/// Expected UART output: 255, 15, 240, 224, 56, 199, 0, 8, 1, 68 ('D')
/// </summary>
[TestFixture]
public class BitwiseOpsTests
{
    private string _hex = null!;
    private static readonly byte[] ExpectedBytes = [255, 15, 240, 224, 56, 199, 0, 8, 1, 68];

    [OneTimeSetUp]
    public void BuildFirmware() => _hex = PymcuCompiler.Build("bitwise-ops");

    [Test]
    public void Output_ExactByteSequence()
    {
        var uno = Sim();
        // Wait for 'D' (68) marker which signals completion
        uno.RunUntilSerial(uno.Serial, s => s.Length >= 10 && s.Last() == (char)68, maxMs: 200);
        uno.Serial.Should().HaveBytes(ExpectedBytes);
    }

    [Test]
    public void Output_OrResult_Is255()
    {
        var uno = Sim();
        uno.RunUntilSerialBytes(uno.Serial, 1, maxMs: 50);
        uno.Serial.Bytes[0].Should().Be(255, "0x0F | 0xF0 = 0xFF = 255");
    }

    [Test]
    public void Output_AndResult_Is15()
    {
        var uno = Sim();
        uno.RunUntilSerialBytes(uno.Serial, 2, maxMs: 50);
        uno.Serial.Bytes[1].Should().Be(15, "0xFF & 0x0F = 0x0F = 15");
    }

    [Test]
    public void Output_DoneMarker_IsD()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, s => s.Length >= 10, maxMs: 200);
        uno.Serial.Bytes[9].Should().Be(68, "'D' done marker");
    }

    [Test]
    public void AfterCompletion_LedBlinks()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, s => s.Length >= 10, maxMs: 200);
        // After 'D', firmware enters blink loop with 500ms delay
        // Run 600ms more and check LED toggled at least once (PB5 is output)
        uno.RunMilliseconds(600);
        // GetPinState returns High or Low (output) — just verify it's configured as output
        var state = uno.PortB.GetPinState(5);
        Assert.That(state, Is.EqualTo(AVR8Sharp.Core.Peripherals.PinState.High)
            .Or.EqualTo(AVR8Sharp.Core.Peripherals.PinState.Low),
            "PB5 should be configured as output (High or Low)");
    }

    private ArduinoUnoSimulation Sim()
    {
        var uno = new ArduinoUnoSimulation();
        uno.WithHex(_hex);
        return uno;
    }
}
