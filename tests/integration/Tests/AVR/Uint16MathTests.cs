using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;

namespace Whipsnake.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for examples/avr/uint16-math.
/// All 6 arithmetic tests should pass, producing "PPPPPP\nDONE\n".
/// </summary>
[TestFixture]
public class Uint16MathTests
{
    private string _hex = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _hex = PymcuCompiler.Build("uint16-math");

    [Test]
    public void AllTests_Pass()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "DONE", maxMs: 200);
        // 6 'P' bytes = 6 × 0x50
        var bytes = uno.Serial.Bytes.Take(6).ToArray();
        bytes.Should().AllSatisfy(b => b.Should().Be(0x50, "'P' = pass"));
    }

    [Test]
    public void NoFailures()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "DONE", maxMs: 200);
        uno.Serial.Should().NotContain("F"); // 'F' = fail byte (0x46)
    }

    [Test]
    public void DoneMarker_Present()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "DONE", maxMs: 200);
        uno.Serial.Should().ContainLine("DONE");
    }

    private ArduinoUnoSimulation Sim()
    {
        var uno = new ArduinoUnoSimulation();
        uno.WithHex(_hex);
        return uno;
    }
}
