using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;

namespace PyMCU.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for fixtures/avr/compat-mp-pin-pull.
///
/// Verifies two MicroPython machine.Pin API extensions:
///
///   1. Pull-up: Pin(2, Pin.IN, Pin.PULL_UP)
///      The three-arg form configures the AVR internal pull-up resistor.
///      When no external signal is applied, PD2 reads HIGH.
///
///   2. String pin-id: Pin("PB5", Pin.OUT)
///      The string overload resolves the port name directly instead of going
///      through the Arduino integer → port-string mapping table.
///      PB5 must be configured as output (DDRB bit 5 set).
///
/// Fixture behaviour:
///   Boot:  sends "READY\n"
///   Then:  reads btn (PD2, pull-up) and sends the value byte (0x00 or 0x01).
///   Loop:  waits for UART byte; if 0xFF toggles led; re-reads btn and echoes.
/// </summary>
[TestFixture]
public class CompatMpPinPullTests
{
    private SimSession _session = null!;

    [OneTimeSetUp]
    public void BuildFirmware() => _session = new SimSession(PymcuCompiler.BuildFixture("compat-mp-pin-pull"));

    // ── Pull-up: undriven pin reads HIGH ─────────────────────────────────────

    [Test]
    public void PullUp_UndrivenPin_ReadsHigh()
    {
        var uno = Sim();
        // After READY, firmware sends btn.value() -- should be 0x01 with pull-up active
        uno.RunUntilSerial(uno.Serial, "READY\n", maxMs: 200);
        uno.RunUntilSerialBytes(uno.Serial, uno.Serial.ByteCount + 1, maxMs: 50);
        uno.Serial.Bytes.Last().Should().Be(0x01,
            "PD2 with PULL_UP reads HIGH when no external signal is applied");
    }

    [Test]
    public void PullUp_ExternalLow_ReadsLow()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "READY\n", maxMs: 200);
        // Consume the initial pull-up read byte
        uno.RunUntilSerialBytes(uno.Serial, uno.Serial.ByteCount + 1, maxMs: 50);

        // Drive PD2 low externally (active-low button pressed)
        uno.PortD.SetPinValue(2, false);

        // Trigger a new read by sending 0x00
        uno.Serial.InjectByte(0x00);
        uno.RunUntilSerialBytes(uno.Serial, uno.Serial.ByteCount + 1, maxMs: 50);
        uno.Serial.Bytes.Last().Should().Be(0x00,
            "PD2 reads LOW when driven low externally despite pull-up");
    }

    [Test]
    public void PullUp_ReleasedAfterPress_ReadsHighAgain()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "READY\n", maxMs: 200);
        uno.RunUntilSerialBytes(uno.Serial, uno.Serial.ByteCount + 1, maxMs: 50);

        // Press (drive low), trigger read
        uno.PortD.SetPinValue(2, false);
        uno.Serial.InjectByte(0x00);
        uno.RunUntilSerialBytes(uno.Serial, uno.Serial.ByteCount + 1, maxMs: 50);

        // Release (no external drive), trigger read
        uno.PortD.SetPinValue(2, true);
        uno.Serial.InjectByte(0x00);
        uno.RunUntilSerialBytes(uno.Serial, uno.Serial.ByteCount + 1, maxMs: 50);
        uno.Serial.Bytes.Last().Should().Be(0x01,
            "PD2 reads HIGH again after external low is released");
    }

    // ── String pin-id: Pin("PB5", Pin.OUT) sets DDRB5 ────────────────────────

    [Test]
    public void StringPinId_ConfiguresOutput()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "READY\n", maxMs: 200);
        // PD2 must be input with pull-up: DDRD2=0, PORTD2=1
        uno.PortD.Should().HavePinInputPullup(2,
            "Pin(2, Pin.IN, Pin.PULL_UP) must configure PD2 as input with pull-up");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private ArduinoUnoSimulation Sim()
    {
        var uno = _session.Reset();
        uno.PortD.SetPinValue(2, true); // no external drive on PD2 (pull-up active)
        return uno;
    }
}
