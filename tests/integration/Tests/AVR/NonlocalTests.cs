using Avr8Sharp.TestKit.Boards;
using FluentAssertions;
using NUnit.Framework;

namespace Whisnake.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for examples/avr/nonlocal.
/// Exercises PEP 3104 nonlocal variable binding in nested @inline functions:
///   - Counter: nested @inline def increment() mutates outer count via nonlocal
///   - Accumulator: nested @inline def add(delta) mutates outer total via nonlocal
/// </summary>
[TestFixture]
public class NonlocalTests
{
    private static string _hex = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _hex = PymcuCompiler.Build("nonlocal");

    private ArduinoUnoSimulation Boot()
    {
        var uno = new ArduinoUnoSimulation();
        uno.WithHex(_hex);
        uno.RunUntilSerial(uno.Serial, "NL\n", maxMs: 200);
        return uno;
    }

    [Test]
    public void Boot_SendsBanner() =>
        Boot().Serial.Text.Should().Contain("NL");

    [Test]
    public void Counter_ThreeIncrements_IsThree()
    {
        // count starts at 0; increment() called 3 times -> count = 3
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("A:03\n"), maxMs: 300);
        uno.Serial.Text.Should().Contain("A:03",
            "count after 3 increments should be 3 = 0x03");
    }

    [Test]
    public void Accumulator_AddTen_IsTen()
    {
        // total starts at 0; add(10) -> total = 10 = 0x0A
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("B:0A\n"), maxMs: 300);
        uno.Serial.Text.Should().Contain("B:0A",
            "total after add(10) should be 10 = 0x0A");
    }

    [Test]
    public void Accumulator_AddTenThenFifteen_IsTwentyFive()
    {
        // total = 10 + 15 = 25 = 0x19
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("C:19\n"), maxMs: 300);
        uno.Serial.Text.Should().Contain("C:19",
            "total after add(10)+add(15) should be 25 = 0x19");
    }
}
