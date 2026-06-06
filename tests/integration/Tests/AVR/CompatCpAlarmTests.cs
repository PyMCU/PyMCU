using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;

namespace PyMCU.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for fixtures/avr/compat-cp-alarm.
/// Verifies the pymcu-circuitpython alarm sleep entry points run on real AVR:
/// alarm.light_sleep_until_alarms() and alarm.exit_and_deep_sleep_until_alarms()
/// each block on a TimeAlarm (~50 ms) and return. Expected UART: "ABCD".
///
/// This also covers nested-class ZCA construction (alarm.time.TimeAlarm, a class
/// nested in a module-level namespace) compiling on-target.
/// </summary>
[TestFixture]
public class CompatCpAlarmTests
{
    private SimSession _session = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _session = new SimSession(PymcuCompiler.BuildFixture("compat-cp-alarm"));

    [Test]
    public void LightSleep_WakesAndContinues()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "B", maxMs: 500);
        uno.Serial.Should().Contain("B", "light_sleep_until_alarms() returns and the program continues");
    }

    [Test]
    public void DeepSleepEntry_ReturnsAndReachesDone()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "D", maxMs: 1000);
        uno.Serial.Text.Should().Contain("ABCD",
            "both sleep entry points run in order and the firmware reaches the done marker");
    }

    private ArduinoUnoSimulation Sim() => _session.Reset();
}
