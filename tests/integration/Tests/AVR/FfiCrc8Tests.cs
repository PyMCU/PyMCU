using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;

namespace PyMCU.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for examples/avr/ffi-crc8.
/// Calls crc8_update() from crc8.c (Dallas/Maxim CRC-8 from avr-libc) via @extern FFI.
///
/// Expected UART output:
///   "CRC8\n"   -- boot banner
///   "R:56\n"   -- CRC-8 of the 7 DS18B20 ROM bytes {0x28,0x11,0x22,0x33,0x44,0x55,0x66}
///   "V:00\n"   -- self-check: feeding the 8th byte (CRC) back always gives 0x00
///   "Z:00\n"   -- crc8_update(0x00, 0x00) = 0x00
///   "OK\n"     -- done
///
/// Each crc8_update() CALL invokes a C function; the full sequence (16 calls)
/// runs in under 2 simulated seconds on the AVR simulator.
/// </summary>
[TestFixture]
public class FfiCrc8Tests
{
    private SimSession _session = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _session = new SimSession(PymcuCompiler.Build("ffi-crc8"));

    [Test]
    public void Boot_SendsBanner()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "CRC8\n", maxMs: 2000);
        uno.Serial.Text.Should().Contain("CRC8");
    }

    [Test]
    public void RomCrc_IsCorrectForDs18b20StyleRom()
    {
        // CRC-8 Dallas of {0x28,0x11,0x22,0x33,0x44,0x55,0x66} = 0x56
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "R:56\n", maxMs: 2000);
        uno.Serial.Text.Should().Contain("R:56",
            "CRC-8 of the 7 DS18B20-style ROM bytes must equal 0x56");
    }

    [Test]
    public void SelfCheck_IsAlwaysZero()
    {
        // Feeding all 8 bytes (7 data + CRC) into the CRC function always yields 0x00.
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "V:00\n", maxMs: 2000);
        uno.Serial.Text.Should().Contain("V:00",
            "CRC-8 self-check (8 bytes including CRC) must be 0x00");
    }

    [Test]
    public void ZeroCheck_IsZero()
    {
        // crc8_update(0x00, 0x00) = 0x00
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "Z:00\n", maxMs: 2000);
        uno.Serial.Text.Should().Contain("Z:00",
            "crc8_update(0, 0) must return 0x00");
    }

    [Test]
    public void AllProbes_Done_Marker_Present()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "OK\n", maxMs: 2000);
        uno.Serial.Text.Should().Contain("OK",
            "firmware must print OK after all CRC probes complete");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private ArduinoUnoSimulation Sim() => _session.Reset();
}
