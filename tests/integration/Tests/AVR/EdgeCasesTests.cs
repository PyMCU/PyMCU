using Avr8Sharp.TestKit.Boards;
using FluentAssertions;
using NUnit.Framework;

namespace PyMCU.IntegrationTests.Tests.AVR;

[TestFixture]
public class EdgeCasesTests
{
    private static string _hex = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _hex = PymcuCompiler.BuildFixture("edge-cases");

    private ArduinoUnoSimulation Boot()
    {
        var uno = new ArduinoUnoSimulation();
        uno.WithHex(_hex);
        uno.RunUntilSerial(uno.Serial, "EDGE\n", maxMs: 200);
        return uno;
    }

    [Test]
    public void Boot_SendsBanner() =>
        Boot().Serial.Text.Should().Contain("EDGE");

    [Test]
    public void ShiftBy7_Gives0x80()
    {
        var uno = Boot();
        // 1 << 7 = 128 = 0x80
        uno.RunUntilSerial(uno.Serial, s => s.Contains("A:80\n"), maxMs: 200);
        uno.Serial.Text.Should().Contain("A:80", "1 << 7 must be 0x80");
    }

    [Test]
    public void XorSelf_GivesZero()
    {
        var uno = Boot();
        // x ^ x = 0, nibble_hex(0) = '0'
        uno.RunUntilSerial(uno.Serial, s => s.Contains("B:0\n"), maxMs: 200);
        uno.Serial.Text.Should().Contain("B:0", "x ^ x must be 0");
    }

    [Test]
    public void AndWithFF_IsIdentity()
    {
        var uno = Boot();
        // 0xCD & 0xFF = 0xCD
        uno.RunUntilSerial(uno.Serial, s => s.Contains("C:CD\n"), maxMs: 200);
        uno.Serial.Text.Should().Contain("C:CD", "0xCD & 0xFF must be 0xCD");
    }

    [Test]
    public void ZeroLessThanOne_IsTrue()
    {
        var uno = Boot();
        // 0 < 1 is true -- sends 'T\n'
        uno.RunUntilSerial(uno.Serial, s => s.Contains("T\n"), maxMs: 200);
        uno.Serial.Text.Should().Contain("T", "0 < 1 must be true");
    }

    [Test]
    public void Uint16_Bit15_ShiftRight15_Gives1()
    {
        var uno = Boot();
        // 0x8000 >> 15 = 1
        uno.RunUntilSerial(uno.Serial, s => s.Contains("D:1\n"), maxMs: 200);
        uno.Serial.Text.Should().Contain("D:1", "0x8000 >> 15 must be 1");
    }
}
