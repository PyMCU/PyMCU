using Avr8Sharp.TestKit.Boards;
using FluentAssertions;
using NUnit.Framework;

namespace Whipsnake.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for examples/avr/ffi-dsp.
/// Exercises multi-file C interop via @extern with two C source files
/// (math_utils.c and filter.c) compiled with avr-gcc and linked via avr-ld.
///
/// Expected UART output:
///   "FFIDSP\n"  -- boot banner
///   "C:64\n"    -- c_clamp8(200, 10, 100) = 100 = 0x64
///   "L:64\n"    -- c_lerp8(0, 200, 128)   = 100 = 0x64
///   "K:64\n"    -- c_scale8(128, 200)     = 100 = 0x64
///   "E:57\n"    -- c_smooth8(50, 200, 64) =  87 = 0x57
///   "D:00\n"    -- c_deadband8(30, 50)    =   0 = 0x00
///   "B:1E\n"    -- c_deadband8(80, 50)    =  30 = 0x1E
///   "OK\n"      -- done
/// </summary>
[TestFixture]
public class FfiDspTests
{
    private static string _hex = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _hex = PymcuCompiler.Build("ffi-dsp");

    private ArduinoUnoSimulation Boot()
    {
        var uno = new ArduinoUnoSimulation();
        uno.WithHex(_hex);
        uno.RunUntilSerial(uno.Serial, "FFIDSP\n", maxMs: 200);
        return uno;
    }

    [Test]
    public void Boot_SendsBanner() =>
        Boot().Serial.Text.Should().Contain("FFIDSP");

    [Test]
    public void Clamp8_200_To_10_100_Returns100()
    {
        // c_clamp8(200, 10, 100) = 100 = 0x64
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("C:64\n"), maxMs: 300);
        uno.Serial.Text.Should().Contain("C:64",
            "c_clamp8(200, 10, 100) should clamp to 100 = 0x64");
    }

    [Test]
    public void Lerp8_0_200_128_Returns100()
    {
        // c_lerp8(0, 200, 128) = 0 + 200*128/255 = 100 = 0x64
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("L:64\n"), maxMs: 300);
        uno.Serial.Text.Should().Contain("L:64",
            "c_lerp8(0, 200, 128) should return ~100 = 0x64");
    }

    [Test]
    public void Scale8_128_200_Returns100()
    {
        // c_scale8(128, 200) = 128*200/255 = 100 = 0x64
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("K:64\n"), maxMs: 300);
        uno.Serial.Text.Should().Contain("K:64",
            "c_scale8(128, 200) should return ~100 = 0x64");
    }

    [Test]
    public void Smooth8_50_200_64_Returns87()
    {
        // c_smooth8(50, 200, 64) = 50 + 150*64/256 = 87 = 0x57
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("E:57\n"), maxMs: 300);
        uno.Serial.Text.Should().Contain("E:57",
            "c_smooth8(50, 200, 64) should return 87 = 0x57");
    }

    [Test]
    public void Deadband8_30_Below_50_Returns0()
    {
        // c_deadband8(30, 50) = 0 (30 < 50) = 0x00
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("D:00\n"), maxMs: 300);
        uno.Serial.Text.Should().Contain("D:00",
            "c_deadband8(30, 50) should return 0 = 0x00 (below deadband)");
    }

    [Test]
    public void Deadband8_80_Above_50_Returns30()
    {
        // c_deadband8(80, 50) = 80 - 50 = 30 = 0x1E
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("B:1E\n"), maxMs: 300);
        uno.Serial.Text.Should().Contain("B:1E",
            "c_deadband8(80, 50) should return 30 = 0x1E");
    }

    [Test]
    public void AllResultsPresent_Done()
    {
        var uno = Boot();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("OK\n"), maxMs: 400);
        uno.Serial.Text.Should().Contain("OK",
            "firmware should print OK after all @extern calls complete");
    }
}
