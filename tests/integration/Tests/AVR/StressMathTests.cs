using Avr8Sharp.TestKit.Boards;
using FluentAssertions;
using NUnit.Framework;

namespace PyMCU.IntegrationTests.Tests.AVR;

[TestFixture]
public class StressMathTests
{
    private static SimSession _session = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _session = new SimSession(PymcuCompiler.BuildFixture("stress-math"));

    private ArduinoUnoSimulation Boot()
    {
        var uno = _session.Reset();
        uno.RunUntilSerial(uno.Serial, "STRESS\n", maxMs: 200);
        return uno;
    }

    [Test]
    public void Boot_SendsBanner() =>
        Boot().Serial.Text.Should().Contain("STRESS");

    [Test]
    public void Overflow_Uint8_WrapsToZero()
    {
        var uno = Boot();
        // Wait for full overflow line: "O:0\n"
        uno.RunUntilSerial(uno.Serial, s => s.Contains("O:0\n"), maxMs: 200);
        uno.Serial.Text.Should().Contain("O:0", "255 + 1 must wrap to 0");
    }

    [Test]
    public void ClampAdd_ClampsToHi()
    {
        var uno = Boot();
        // clamp_add(200, 40, 10, 230): 200+40=240 > 230, clamps to 230 = 0xE6
        uno.RunUntilSerial(uno.Serial, s => s.Contains("C:E6\n"), maxMs: 200);
        uno.Serial.Text.Should().Contain("C:E6", "200+40=240 clamped to 230=0xE6");
    }

    [Test]
    public void Polynomial_ComputesCorrectly()
    {
        var uno = Boot();
        // poly(5) = 25 + 15 + 7 = 47 = 0x2F
        uno.RunUntilSerial(uno.Serial, s => s.Contains("P:2F\n"), maxMs: 200);
        uno.Serial.Text.Should().Contain("P:2F", "poly(5) = 47 = 0x2F");
    }

    [Test]
    public void Overflow_Uint16_WrapsToZero()
    {
        var uno = Boot();
        // 65535 + 1 wraps to 0 = 0x0000
        uno.RunUntilSerial(uno.Serial, s => s.Contains("W:0000\n"), maxMs: 200);
        uno.Serial.Text.Should().Contain("W:0000", "65535 + 1 must wrap to 0");
    }
}
