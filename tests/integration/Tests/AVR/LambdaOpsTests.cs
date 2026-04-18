using Avr8Sharp.TestKit.Boards;
using FluentAssertions;
using NUnit.Framework;

namespace PyMCU.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for examples/avr/lambda-ops.
/// Exercises:
///   F9: lambda expressions (no closure capture, inlined at call site)
///   F1: raw string literals r"..."
/// </summary>
[TestFixture]
public class LambdaOpsTests
{
    private static SimSession _session = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _session = new SimSession(PymcuCompiler.BuildFixture("lambda-ops"));

    private ArduinoUnoSimulation Boot()
    {
        var uno = _session.Reset();
        uno.RunUntilSerial(uno.Serial, "LB\n", maxMs: 200);
        return uno;
    }

    [Test]
    public void Boot_SendsBanner() =>
        Boot().Serial.Text.Should().Contain("LB");

    [Test]
    public void Lambda_Double_IsCorrect()
    {
        // double = lambda x: x * 2 + 1; double(2) = 5 = 0x05
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("D:05"), maxMs: 300);
        uno.Serial.Text.Should().Contain("D:05",
            "lambda x: x*2+1 applied to 2 should yield 5 = 0x05");
    }

    [Test]
    public void Lambda_Triple_IsCorrect()
    {
        // triple = lambda x: x * x; triple(3) = 9 = 0x09
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("T:09"), maxMs: 300);
        uno.Serial.Text.Should().Contain("T:09",
            "lambda x: x*x applied to 3 should yield 9 = 0x09");
    }

    [Test]
    public void RawString_Backslash_IsBackslashByte()
    {
        // raw_str = r"\n" -> first char is backslash = 0x5C, second is 'n' = 0x6E
        // We print ord of first char -> 0x5C
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("R:5C"), maxMs: 300);
        uno.Serial.Text.Should().Contain("R:5C",
            "r\"\\n\"[0] should be backslash = 0x5C (raw string, no escape processing)");
    }
}
