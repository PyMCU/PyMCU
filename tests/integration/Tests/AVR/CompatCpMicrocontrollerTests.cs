using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;

namespace PyMCU.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for fixtures/avr/compat-cp-microcontroller.
/// Exercises three pymcu-circuitpython features together on real AVR:
///   - microcontroller.nvm        (EEPROM-backed persistent storage)
///   - microcontroller.watchdog   (runtime-timeout arming via wdt_arm_rt)
///   - microcontroller.cpu.reset_reason / ResetReason (MCUSR decode)
///
/// Expected UART: [0x5A, 0x00, 0x44].
///   Byte 0 (0x5A): a value written to and read back from nvm[0] -- proves an
///                  EEPROM write/read round-trip on hardware.
///   Byte 1 (0x00): cpu.reset_reason == ResetReason.POWER_ON -- the simulator
///                  boots with MCUSR.PORF set, decoded by the MCUSR reader.
///   Byte 2 (0x44, 'D'): the done marker, reached only after the watchdog
///                  arm/feed/disable sequence ran without resetting the chip.
///
/// Each test builds its own <see cref="ArduinoUnoSimulation"/> rather than sharing
/// a <see cref="SimSession"/>: this fixture writes EEPROM (via nvm), and the
/// AVR8Sharp EEPROM peripheral keeps write-timing counters that SimSession.Reset()
/// does not clear, which would hang every test after the first (see EepromTests).
/// </summary>
[TestFixture]
public class CompatCpMicrocontrollerTests
{
    private const byte ResetReasonPowerOn = 0x00;

    private string _hex = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _hex = PymcuCompiler.BuildFixture("compat-cp-microcontroller");

    private ArduinoUnoSimulation Sim()
    {
        var uno = new ArduinoUnoSimulation();
        uno.WithHex(_hex);
        return uno;
    }

    [Test]
    public void Nvm_RoundTripsThroughEeprom()
    {
        var uno = Sim();
        uno.RunUntilSerialBytes(uno.Serial, 1, maxMs: 100);
        uno.Serial.Bytes[0].Should().Be(0x5A, "nvm[0] write then read returns the stored byte");
    }

    [Test]
    public void ResetReason_DecodesPowerOnFromMcusr()
    {
        var uno = Sim();
        uno.RunUntilSerialBytes(uno.Serial, 2, maxMs: 150);
        uno.Serial.Bytes[1].Should().Be(ResetReasonPowerOn,
            "a fresh power-on sets MCUSR.PORF, which reset_reason decodes to POWER_ON");
    }

    [Test]
    public void Watchdog_ArmFeedDisable_RunsWithoutResettingAndReachesDone()
    {
        var uno = Sim();
        uno.RunUntilSerialBytes(uno.Serial, 3, maxMs: 200);
        uno.Serial.Bytes[2].Should().Be(0x44,
            "the 'D' done marker is only sent if watchdog arm/feed/disable ran without resetting");
    }

    [Test]
    public void ExactOutput_NvmThenResetReasonThenDone()
    {
        var uno = Sim();
        uno.RunUntilSerialBytes(uno.Serial, 3, maxMs: 200);
        uno.Serial.Should().HaveBytes([0x5A, ResetReasonPowerOn, 0x44]);
    }
}
