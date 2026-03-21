using Avr8Sharp.TestKit.Boards;
using FluentAssertions;
using NUnit.Framework;

namespace PyMCU.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for examples/avr/slice-ops.
/// Exercises compile-time array slice indexing (PEP 197, F8):
///   arr[start:stop], arr[start:stop:step], arr[:stop], arr[start:].
/// </summary>
[TestFixture]
public class SliceOpsTests
{
    private static string _hex = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _hex = PymcuCompiler.Build("slice-ops");

    private ArduinoUnoSimulation Boot()
    {
        var uno = new ArduinoUnoSimulation();
        uno.WithHex(_hex);
        uno.RunUntilSerial(uno.Serial, "SL\n", maxMs: 200);
        return uno;
    }

    [Test]
    public void Boot_SendsBanner() =>
        Boot().Serial.Text.Should().Contain("SL");

    [Test]
    public void SliceStartStop_FirstElement_IsCorrect()
    {
        // first: uint8[4] = src[0:4]  →  first[0] = src[0] = 10 = 0x0A
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("A:0A\n"), maxMs: 300);
        uno.Serial.Text.Should().Contain("A:0A",
            "first[0] = src[0] = 10 = 0x0A");
    }

    [Test]
    public void SliceStartStop_LastChunk_LastElement_IsCorrect()
    {
        // last: uint8[4] = src[4:8]  →  last[3] = src[7] = 80 = 0x50
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("B:50\n"), maxMs: 300);
        uno.Serial.Text.Should().Contain("B:50",
            "last[3] = src[7] = 80 = 0x50");
    }

    [Test]
    public void SliceStep2_FirstElement_IsCorrect()
    {
        // even: uint8[4] = src[0:8:2]  →  even[0] = src[0] = 10 = 0x0A
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("C:0A\n"), maxMs: 300);
        uno.Serial.Text.Should().Contain("C:0A",
            "even[0] = src[0] = 10 = 0x0A");
    }

    [Test]
    public void SliceStep2_SecondElement_SkipsOne()
    {
        // even: uint8[4] = src[0:8:2]  →  even[1] = src[2] = 30 = 0x1E
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("D:1E\n"), maxMs: 300);
        uno.Serial.Text.Should().Contain("D:1E",
            "even[1] = src[2] = 30 = 0x1E (step=2 skips index 1)");
    }
}
