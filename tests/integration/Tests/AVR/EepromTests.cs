using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;

namespace PyMCU.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for examples/avr/eeprom.
/// Tests EEPROM.write() and EEPROM.read() via the AVR8Sharp EEPROM simulator.
/// Expected sequence: "EEPROM TEST\n" then "EEPROM OK\n".
///
/// Each test creates its own <see cref="ArduinoUnoSimulation"/> rather than sharing
/// a <see cref="SimSession"/>.  The AVR8Sharp EEPROM peripheral (<c>AvrEeprom</c>)
/// holds internal write-timing counters (<c>_writeCompleteCycles</c> /
/// <c>_writeEnabledCycles</c>) that are not cleared by <c>Cpu.Reset()</c>.
/// Resetting <c>Cpu.Cycles</c> to 0 while those counters retain stale values from
/// a previous run causes the next EEPROM write to be silently skipped and EEPE to
/// be left high, hanging the firmware.  An isolated simulator avoids the issue
/// entirely and produces a clean power-on EEPROM state for every test.
/// See docs/bugs/avr8sharp-eeprom-reset.md for the upstream bug report.
/// </summary>
[TestFixture]
public class EepromTests
{
    private string _hex = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _hex = PymcuCompiler.Build("eeprom");

    private ArduinoUnoSimulation Sim()
    {
        var uno = new ArduinoUnoSimulation();
        uno.WithHex(_hex);
        return uno;
    }

    [Test]
    public void Boot_PrintsEepromTest()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "EEPROM TEST", maxMs: 1000);
        uno.Serial.Should().Contain("EEPROM TEST");
    }

    [Test]
    public void WriteAndReadBack_ReturnsOk()
    {
        // The firmware writes 4 known bytes, reads them back, and prints
        // "EEPROM OK" if the values match, "EEPROM FAIL" otherwise.
        // The EEPROM write latency is ~3.4ms per byte; 4 writes = ~14ms.
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, s => s.Contains("EEPROM OK") || s.Contains("EEPROM FAIL"),
            maxMs: 200);
        uno.Serial.Should().Contain("EEPROM OK");
        uno.Serial.Should().NotContain("EEPROM FAIL");
    }

    [Test]
    public void WriteAndReadBack_DoesNotHangOrFail()
    {
        var uno = Sim();
        // Should reach "DONE" (which is the while(True) after "EEPROM OK")
        // by timing out gracefully — the simulation won't run forever,
        // but the UART output must include "EEPROM OK".
        uno.RunUntilSerial(uno.Serial, "EEPROM OK", maxMs: 300);
        uno.Serial.Text.Should().Contain("EEPROM OK");
    }
}
