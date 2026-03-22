using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;

namespace Whipsnake.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for examples/avr/shift-register (bit-bang SPI to 74HC595).
/// Sends each pattern byte over UART; first pattern = 0x01, rotates left.
/// PB0=data, PB1=clock, PB2=latch.
/// </summary>
[TestFixture]
public class ShiftRegisterTests
{
    private string _hex = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _hex = PymcuCompiler.Build("shift-register");

    [Test]
    public void FirstPattern_Is0x01()
    {
        var uno = Sim();
        uno.RunUntilSerialBytes(uno.Serial, 1, maxMs: 500);
        uno.Serial.Bytes[0].Should().Be(0x01, "running light starts at bit 0");
    }

    [Test]
    public void SecondPattern_Is0x02()
    {
        var uno = Sim();
        uno.RunUntilSerialBytes(uno.Serial, 2, maxMs: 1000);
        uno.Serial.Bytes[1].Should().Be(0x02, "rotate left: 0x01 → 0x02");
    }

    [Test]
    public void EightPatterns_FullRotation()
    {
        var uno = Sim();
        uno.RunUntilSerialBytes(uno.Serial, 8, maxMs: 3000);
        // After 8 rotations: 0x01, 0x02, 0x04, 0x08, 0x10, 0x20, 0x40, 0x80
        byte[] expected = [0x01, 0x02, 0x04, 0x08, 0x10, 0x20, 0x40, 0x80];
        uno.Serial.Should().HaveBytesAt(0, expected);
    }

    [Test]
    public void PB2_IsLatch_ConfiguredAsOutput()
    {
        var uno = Sim();
        uno.RunMilliseconds(50);
        // PB2 (latch) should be output — either High or Low
        var state = uno.PortB.GetPinState(2);
        Assert.That(state, Is.EqualTo(AVR8Sharp.Core.Peripherals.PinState.High)
            .Or.EqualTo(AVR8Sharp.Core.Peripherals.PinState.Low),
            "PB2 should be configured as output");
    }

    private ArduinoUnoSimulation Sim()
    {
        var uno = new ArduinoUnoSimulation();
        uno.WithHex(_hex);
        return uno;
    }
}
