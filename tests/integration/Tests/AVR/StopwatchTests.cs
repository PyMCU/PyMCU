using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;
using AVR8Sharp.Core.Peripherals;

namespace Whipsnake.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for examples/avr/stopwatch.
/// Three simultaneous ISRs: INT0 start/stop, INT1 reset, Timer0 OVF tick.
/// Tests: 3 @interrupt decorators, GPIOR0 with 3 bit flags, uint16 seconds,
///        start/stop toggle, reset, coexistence of all three ISRs.
/// </summary>
[TestFixture]
public class StopwatchTests
{
    private string _hex = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _hex = PymcuCompiler.Build("stopwatch");

    [Test]
    public void Boot_SendsBanner()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "STOPWATCH");
        uno.Serial.Should().ContainLine("STOPWATCH");
    }

    [Test]
    public void Start_ThenWait_ReceivesSeconds()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "STOPWATCH\n");

        // INT0 falling edge → start
        uno.PortD.SetPinValue(2, true);
        uno.RunMilliseconds(1);
        uno.PortD.SetPinValue(2, false);
        uno.RunMilliseconds(5);

        var before = uno.Serial.ByteCount;

        // Wait ~1 second of sim time → should receive second count byte
        uno.RunUntilSerialBytes(uno.Serial, before + 2, maxMs: 1500);

        uno.Serial.Bytes[before].Should().Be(1, "first second = 1");
        uno.Serial.Bytes[before + 1].Should().Be((byte)'\n');
    }

    [Test]
    public void Stop_HaltsSecondCounting()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "STOPWATCH\n");

        // Start
        uno.PortD.SetPinValue(2, true); uno.RunMilliseconds(1);
        uno.PortD.SetPinValue(2, false); uno.RunMilliseconds(5);

        var before = uno.Serial.ByteCount;
        uno.RunUntilSerialBytes(uno.Serial, before + 2, maxMs: 1500);
        uno.Serial.Bytes[before].Should().Be(1, "first second counted");

        // Stop
        uno.PortD.SetPinValue(2, true); uno.RunMilliseconds(1);
        uno.PortD.SetPinValue(2, false); uno.RunMilliseconds(5);

        var afterStop = uno.Serial.ByteCount;
        // Run for another 1.5s — stopped means no more second bytes
        uno.RunMilliseconds(1500);
        uno.Serial.ByteCount.Should().Be(afterStop, "no new bytes while stopped");
    }

    [Test]
    public void Reset_SendsZeroAndStops()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "STOPWATCH\n");

        // Start, wait 1 second
        uno.PortD.SetPinValue(2, true); uno.RunMilliseconds(1);
        uno.PortD.SetPinValue(2, false);
        uno.RunUntilSerialBytes(uno.Serial, uno.Serial.ByteCount + 2, maxMs: 1500);

        var before = uno.Serial.ByteCount;

        // INT1 falling edge → reset
        uno.PortD.SetPinValue(3, true); uno.RunMilliseconds(1);
        uno.PortD.SetPinValue(3, false);
        uno.RunUntilSerialBytes(uno.Serial, before + 2, maxMs: 200);

        uno.Serial.Bytes[before].Should().Be(0, "reset sends 0");
        uno.Serial.Bytes[before + 1].Should().Be((byte)'\n');

        // After reset, stopped — no more bytes for 1.5s
        var afterReset = uno.Serial.ByteCount;
        uno.RunMilliseconds(1500);
        uno.Serial.ByteCount.Should().Be(afterReset, "stopped after reset");
    }

    [Test]
    public void StartStopStart_CountsContinuously()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "STOPWATCH\n");

        // Start → wait ~1s → Stop → Start again → wait ~1s more
        uno.PortD.SetPinValue(2, true); uno.RunMilliseconds(1);
        uno.PortD.SetPinValue(2, false);
        var before = uno.Serial.ByteCount;
        uno.RunUntilSerialBytes(uno.Serial, before + 2, maxMs: 1500);
        var count1 = uno.Serial.Bytes[before]; // should be 1

        // Stop
        uno.PortD.SetPinValue(2, true); uno.RunMilliseconds(1);
        uno.PortD.SetPinValue(2, false); uno.RunMilliseconds(5);

        // Start again — should continue from where it stopped
        uno.PortD.SetPinValue(2, true); uno.RunMilliseconds(1);
        uno.PortD.SetPinValue(2, false);
        var before2 = uno.Serial.ByteCount;
        uno.RunUntilSerialBytes(uno.Serial, before2 + 2, maxMs: 1500);
        var count2 = uno.Serial.Bytes[before2];

        count1.Should().Be(1, "first start gives second=1");
        count2.Should().BeGreaterThan(count1, "continuing from paused state increments further");
    }

    private ArduinoUnoSimulation Sim()
    {
        var uno = new ArduinoUnoSimulation();
        uno.WithHex(_hex);
        uno.PortD.SetPinValue(2, true); // INT0 button released
        uno.PortD.SetPinValue(3, true); // INT1 button released
        return uno;
    }
}
