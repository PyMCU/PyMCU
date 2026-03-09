using Avr8Sharp.TestKit.Boards;
using FluentAssertions;
using NUnit.Framework;

namespace PyMCU.IntegrationTests.Tests.AVR;

[TestFixture]
public class ArrayOpsTests
{
    private static string _hex = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _hex = PymcuCompiler.Build("array-ops");

    private ArduinoUnoSimulation Boot()
    {
        var uno = new ArduinoUnoSimulation();
        uno.WithHex(_hex);
        uno.RunUntilSerial(uno.Serial, "ARRAY\n", maxMs: 200);
        return uno;
    }

    [Test]
    public void Boot_SendsBanner() =>
        Boot().Serial.Text.Should().Contain("ARRAY");

    [Test]
    public void Sum_IsCorrect()
    {
        var uno = Boot();
        // 10+20+30+40+50+60+70+80 = 360 = 0x168; low byte = 0x68
        uno.RunUntilSerial(uno.Serial, s => s.Contains("S:68\n"), maxMs: 300);
        uno.Serial.Text.Should().Contain("S:68", "sum of [10..80 step 10] is 360, low byte 0x68");
    }

    [Test]
    public void Min_IsCorrect()
    {
        var uno = Boot();
        // min of [10, 20, 30, 40, 50, 60, 70, 80] = 10 = 0x0A
        uno.RunUntilSerial(uno.Serial, s => s.Contains("M:0A\n"), maxMs: 300);
        uno.Serial.Text.Should().Contain("M:0A", "minimum of array is 10 = 0x0A");
    }
}
