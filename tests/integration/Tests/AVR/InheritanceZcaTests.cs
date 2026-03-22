using Avr8Sharp.TestKit.Boards;
using FluentAssertions;
using NUnit.Framework;

namespace Whipsnake.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for examples/avr/inheritance-zca.
/// Exercises:
///   - Single-level ZCA class inheritance: LED(GPIODevice) inherits on()/off()/read()
///   - Function overloading by type: encode(uint8) vs encode(uint16)
/// </summary>
[TestFixture]
public class InheritanceZcaTests
{
    private static string _hex = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _hex = PymcuCompiler.Build("inheritance-zca");

    private ArduinoUnoSimulation Boot()
    {
        var uno = new ArduinoUnoSimulation();
        uno.WithHex(_hex);
        uno.RunUntilSerial(uno.Serial, "IZ\n", maxMs: 200);
        return uno;
    }

    [Test]
    public void Boot_SendsBanner() =>
        Boot().Serial.Text.Should().Contain("IZ");

    [Test]
    public void Inheritance_OnReadOff_Works()
    {
        // LED inherits on()/off()/read() from GPIODevice.
        // After on() the output latch bit is 1; read() returns 1; off() clears it.
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("A:1\n"), maxMs: 300);
        uno.Serial.Text.Should().Contain("A:1",
            "LED.read() after on() should return 1 (output latch high)");
    }

    [Test]
    public void Overload_Uint8_ReturnsCorrectValue()
    {
        // encode(uint8=0xAB) → 0xAB
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("B:AB\n"), maxMs: 300);
        uno.Serial.Text.Should().Contain("B:AB",
            "encode(uint8=0xAB) should return 0xAB");
    }

    [Test]
    public void Overload_Uint16_ReturnsCorrectValue()
    {
        // encode(uint16=0x1234) → high=0x12, low=0x34 → "1234"
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("C:1234\n"), maxMs: 300);
        uno.Serial.Text.Should().Contain("C:1234",
            "encode(uint16=0x1234) should produce high=0x12, low=0x34");
    }
}
