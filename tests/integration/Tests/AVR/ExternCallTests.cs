using Avr8Sharp.TestKit.Boards;
using FluentAssertions;
using NUnit.Framework;

namespace Whipsnake.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for examples/avr/extern-call.
/// Exercises C interop via @extern decorator:
///   c_mul8(3, 10)             -> 30 = 0x1E
///   c_add_saturate(200, 100)  -> 255 (saturated) = 0xFF
///   c_add_saturate(4, 6)      -> 10 = 0x0A
///
/// The C functions live in c_src/math_helper.c and are compiled with avr-gcc
/// then linked with the Whipsnake firmware via avr-ld (AvrgasToolchain).
/// </summary>
[TestFixture]
public class ExternCallTests
{
    private static string _hex = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _hex = PymcuCompiler.Build("extern-call");

    private ArduinoUnoSimulation Boot()
    {
        var uno = new ArduinoUnoSimulation();
        uno.WithHex(_hex);
        uno.RunUntilSerial(uno.Serial, "EXTERN\n", maxMs: 200);
        return uno;
    }

    [Test]
    public void Boot_SendsBanner() =>
        Boot().Serial.Text.Should().Contain("EXTERN");

    [Test]
    public void CMul8_3x10_Returns30()
    {
        // c_mul8(3, 10) = 30 = 0x1E
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("M:1E\n"), maxMs: 300);
        uno.Serial.Text.Should().Contain("M:1E",
            "c_mul8(3, 10) should return 30 = 0x1E");
    }

    [Test]
    public void CAddSaturate_200Plus100_ClampedTo255()
    {
        // c_add_saturate(200, 100) = 255 (saturated) = 0xFF
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("S:FF\n"), maxMs: 300);
        uno.Serial.Text.Should().Contain("S:FF",
            "c_add_saturate(200, 100) should saturate to 255 = 0xFF");
    }

    [Test]
    public void CAddSaturate_4Plus6_Returns10()
    {
        // c_add_saturate(4, 6) = 10 = 0x0A
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("A:0A\n"), maxMs: 300);
        uno.Serial.Text.Should().Contain("A:0A",
            "c_add_saturate(4, 6) should return 10 = 0x0A");
    }

    [Test]
    public void AllResultsPresent_Done()
    {
        // Final "OK\n" confirms all @extern calls completed
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("OK\n"), maxMs: 400);
        uno.Serial.Text.Should().Contain("OK",
            "firmware should print OK after all @extern calls");
    }
}
