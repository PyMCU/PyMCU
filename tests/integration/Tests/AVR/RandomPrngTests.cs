using Avr8Sharp.TestKit.Boards;
using FluentAssertions;
using NUnit.Framework;

namespace PyMCU.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for the random PRNG (pymcu.random module).
/// Verifies: determinism via randomSeed(), range contracts for random(n)
/// and random2(lo, hi), and that consecutive calls produce different values.
/// </summary>
[TestFixture]
public class RandomPrngTests
{
    private static SimSession _session = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _session = new SimSession(PymcuCompiler.BuildFixture("random-prng"));

    private ArduinoUnoSimulation Boot()
    {
        var uno = _session.Reset();
        uno.RunUntilSerial(uno.Serial, "RN\n", maxMs: 200);
        return uno;
    }

    // Wait until prefix + 2 hex digits appear in the serial buffer.
    private static bool HasHexLine(string text, string prefix)
    {
        var idx = text.IndexOf(prefix, StringComparison.Ordinal);
        return idx >= 0 && idx + prefix.Length + 2 <= text.Length;
    }

    private static int ParseHexLine(string text, string prefix)
    {
        var idx = text.IndexOf(prefix, StringComparison.Ordinal);
        if (idx < 0 || idx + prefix.Length + 2 > text.Length) return -1;
        var hex = text.Substring(idx + prefix.Length, 2);
        return Convert.ToInt32(hex, 16);
    }

    [Test]
    public void Boot_SendsBanner() =>
        Boot().Serial.Text.Should().Contain("RN");

    [Test]
    public void RandomN_FirstValue_InRange()
    {
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => HasHexLine(s, "A:"), maxMs: 300);
        var a = ParseHexLine(uno.Serial.Text, "A:");
        a.Should().BeInRange(0, 99, "random(100) must return a value in [0, 100)");
    }

    [Test]
    public void RandomN_ConsecutiveCalls_AreDifferent()
    {
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => HasHexLine(s, "B:"), maxMs: 300);
        var a = ParseHexLine(uno.Serial.Text, "A:");
        var b = ParseHexLine(uno.Serial.Text, "B:");
        a.Should().NotBe(b, "consecutive random(100) calls with the same seed should differ");
    }

    [Test]
    public void Random2_InBoundedRange()
    {
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => HasHexLine(s, "C:"), maxMs: 300);
        var c = ParseHexLine(uno.Serial.Text, "C:");
        c.Should().BeInRange(10, 49, "random2(10, 50) must return a value in [10, 50)");
    }

    [Test]
    public void RandomSeed_SameSeed_GivesSameFirstValue()
    {
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => HasHexLine(s, "D:"), maxMs: 300);
        var a = ParseHexLine(uno.Serial.Text, "A:");
        var d = ParseHexLine(uno.Serial.Text, "D:");
        d.Should().Be(a, "re-seeding with the same value must reproduce the same sequence");
    }
}
