using Avr8Sharp.TestKit.Boards;
using FluentAssertions;
using NUnit.Framework;

namespace PyMCU.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for the map_range() and constrain() math utilities.
/// map_range(x, in_lo, in_hi, out_lo, out_hi) -- linear range mapping
/// constrain(x, lo, hi)                       -- clamp to [lo, hi]
/// </summary>
[TestFixture]
public class MapRangeTests
{
    private static SimSession _session = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _session = new SimSession(PymcuCompiler.BuildFixture("map-range"));

    private ArduinoUnoSimulation Boot()
    {
        var uno = _session.Reset();
        uno.RunUntilSerial(uno.Serial, "MR\n", maxMs: 200);
        return uno;
    }

    [Test]
    public void Boot_SendsBanner() =>
        Boot().Serial.Text.Should().Contain("MR");

    [Test]
    public void MapRange_Midpoint_IsIdentity()
    {
        // map_range(128, 0, 255, 0, 255) == 128 = 0x80
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("A:80\n"), maxMs: 300);
        uno.Serial.Text.Should().Contain("A:80", "map_range(128,0,255,0,255) should be 128");
    }

    [Test]
    public void MapRange_MaxInput_IsMaxOutput()
    {
        // map_range(255, 0, 255, 0, 255) == 255 = 0xFF
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("B:FF\n"), maxMs: 300);
        uno.Serial.Text.Should().Contain("B:FF", "map_range(255,0,255,0,255) should be 255");
    }

    [Test]
    public void MapRange_HalvedOutputRange_IsHalfValue()
    {
        // map_range(128, 0, 255, 0, 128) == 64 = 0x40
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("C:40\n"), maxMs: 300);
        uno.Serial.Text.Should().Contain("C:40", "map_range(128,0,255,0,128) should be 64");
    }

    [Test]
    public void MapRange_ZeroInput_IsZeroOutput()
    {
        // map_range(0, 0, 255, 0, 255) == 0 = 0x00
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("D:00\n"), maxMs: 300);
        uno.Serial.Text.Should().Contain("D:00", "map_range(0,0,255,0,255) should be 0");
    }

    [Test]
    public void Constrain_InRange_ReturnsValue()
    {
        // constrain(10, 0, 20) == 10 = 0x0A
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("E:0A\n"), maxMs: 300);
        uno.Serial.Text.Should().Contain("E:0A", "constrain(10,0,20) should return 10");
    }

    [Test]
    public void Constrain_BelowLo_ClampsToLo()
    {
        // constrain(0, 5, 20) == 5 = 0x05
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("F:05\n"), maxMs: 300);
        uno.Serial.Text.Should().Contain("F:05", "constrain(0,5,20) should clamp to 5");
    }

    [Test]
    public void Constrain_AboveHi_ClampsToHi()
    {
        // constrain(50, 0, 20) == 20 = 0x14
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("G:14\n"), maxMs: 300);
        uno.Serial.Text.Should().Contain("G:14", "constrain(50,0,20) should clamp to 20");
    }
}
