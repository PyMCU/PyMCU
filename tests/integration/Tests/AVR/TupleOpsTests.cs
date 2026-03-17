using Avr8Sharp.TestKit.Boards;
using FluentAssertions;
using NUnit.Framework;

namespace PyMCU.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for examples/avr/tuple-ops.
/// Exercises:
///   - Literal tuple unpacking: a, b = (3, 7)
///   - Inline multi-return: quot, rem = divmod8(10, 3)
///   - enumerate() over a constant list
///   - enumerate() over range(N)
/// </summary>
[TestFixture]
public class TupleOpsTests
{
    private static string _hex = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _hex = PymcuCompiler.Build("tuple-ops");

    private ArduinoUnoSimulation Boot()
    {
        var uno = new ArduinoUnoSimulation();
        uno.WithHex(_hex);
        uno.RunUntilSerial(uno.Serial, "TO\n", maxMs: 200);
        return uno;
    }

    [Test]
    public void Boot_SendsBanner() =>
        Boot().Serial.Text.Should().Contain("TO");

    [Test]
    public void LiteralTupleUnpack_AssignsCorrectValues()
    {
        // a, b = (3, 7)  → A:03 B:07
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("B:07\n"), maxMs: 300);
        uno.Serial.Text.Should().Contain("A:03", "literal unpack a=3");
        uno.Serial.Text.Should().Contain("B:07", "literal unpack b=7");
    }

    [Test]
    public void InlineMultiReturn_DivMod8_QuotientCorrect()
    {
        // divmod8(10, 3) → quotient=3 → Q:03
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("Q:03\n"), maxMs: 300);
        uno.Serial.Text.Should().Contain("Q:03",
            "divmod8(10,3) quotient should be 3");
    }

    [Test]
    public void InlineMultiReturn_DivMod8_RemainderCorrect()
    {
        // divmod8(10, 3) → remainder=1 → R:01
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("R:01\n"), maxMs: 300);
        uno.Serial.Text.Should().Contain("R:01",
            "divmod8(10,3) remainder should be 1");
    }

    [Test]
    public void EnumerateList_IndexSumCorrect()
    {
        // enumerate([10,20,30]): indices 0+1+2 = 3 → I:03
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("I:03\n"), maxMs: 300);
        uno.Serial.Text.Should().Contain("I:03",
            "enumerate([10,20,30]) index sum should be 3");
    }

    [Test]
    public void EnumerateList_ValueSumCorrect()
    {
        // enumerate([10,20,30]): values 10+20+30 = 60 = 0x3C → X:3C
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("X:3C\n"), maxMs: 300);
        uno.Serial.Text.Should().Contain("X:3C",
            "enumerate([10,20,30]) value sum should be 60=0x3C");
    }

    [Test]
    public void EnumerateRange_IndexSumCorrect()
    {
        // enumerate(range(3)): indices 0+1+2 = 3 → J:03
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("J:03\n"), maxMs: 300);
        uno.Serial.Text.Should().Contain("J:03",
            "enumerate(range(3)) index sum should be 3");
    }

    [Test]
    public void EnumerateRange_ValueSumCorrect()
    {
        // enumerate(range(3)): values 0+1+2 = 3 → Y:03
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("Y:03\n"), maxMs: 300);
        uno.Serial.Text.Should().Contain("Y:03",
            "enumerate(range(3)) value sum should be 3");
    }
}
