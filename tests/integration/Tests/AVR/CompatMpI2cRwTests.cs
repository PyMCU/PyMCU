using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;
using AVR8Sharp.Core.Peripherals;

namespace PyMCU.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for fixtures/avr/compat-mp-i2c-rw.
/// Verifies machine.I2C.writeto(addr, buf, n) and readfrom_into(addr, buf, n)
/// from the pymcu-micropython compat layer.
///
/// writeto(addr, buf, n) deviation:
///   MicroPython: i2c.writeto(addr, bytes_obj)  -- infers len from object
///   PyMCU:       i2c.writeto(addr, buf, n)     -- explicit count required
///
/// readfrom_into(addr, buf, n) deviation:
///   MicroPython: i2c.readfrom(addr, nbytes) -> bytes  (GC-allocated)
///                i2c.readfrom_into(addr, buf)         (fills up to len(buf))
///   PyMCU:       i2c.readfrom_into(addr, buf, n)      (caller buffer + explicit count)
/// </summary>
[TestFixture]
public class CompatMpI2cRwTests
{
    private static string _hex = null!;

    [OneTimeSetUp]
    public void BuildFirmware() =>
        _hex = PymcuCompiler.BuildFixture("compat-mp-i2c-rw");

    [Test]
    public void Boot_SendsReadyBanner()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "READY", maxMs: 300);
        uno.Serial.Should().Contain("READY");
    }

    [Test]
    public void Writeto_MultiByte_CompletesWithNoDevice()
    {
        // No device attached -- SLA+W is NACK'd, but writeto completes without crashing.
        // Firmware echoes 'W' (0x57) after the call.
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "READY\n", maxMs: 300);
        var after = uno.Serial.ByteCount;

        uno.Serial.InjectByte((byte)'W');

        uno.RunUntilSerialBytes(uno.Serial, after + 1, maxMs: 5000);
        uno.Serial.Bytes[after].Should().Be((byte)'W', "writeto completed and echoed 'W'");
    }

    [Test]
    public void Writeto_MultiByte_DeviceSees3Bytes()
    {
        // Attach a stub device at 0x48 that records received bytes.
        var uno = SimWithRecorder(0x48, out var recorder);
        uno.RunUntilSerial(uno.Serial, "READY\n", maxMs: 300);
        var after = uno.Serial.ByteCount;

        uno.Serial.InjectByte((byte)'W');

        uno.RunUntilSerialBytes(uno.Serial, after + 1, maxMs: 5000);
        recorder.ReceivedBytes.Should().Equal(new byte[] { 0xAA, 0xBB, 0xCC },
            "three bytes written in order");
    }

    [Test]
    public void ReadfromInto_NoDevice_Returns0()
    {
        // No device -- returns 0 (NACK on SLA+R). Buffer stays at initial values.
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "READY\n", maxMs: 300);
        var after = uno.Serial.ByteCount;

        uno.Serial.InjectByte((byte)'R');

        // Expect: n=0, then 3 bytes from buf (uninitialised -- all zeros from bytearray)
        uno.RunUntilSerialBytes(uno.Serial, after + 4, maxMs: 5000);
        var resp = uno.Serial.Bytes.Skip(after).Take(4).ToArray();
        resp[0].Should().Be(0, "readfrom_into returns 0 when device NACKs");
    }

    [Test]
    public void ReadfromInto_WithDevice_Receives3Bytes()
    {
        // Stub device at 0x48 sends 0x11, 0x22, 0x33 when read.
        var uno = SimWithSender(0x48, 0x11, 0x22, 0x33);
        uno.RunUntilSerial(uno.Serial, "READY\n", maxMs: 300);
        var after = uno.Serial.ByteCount;

        uno.Serial.InjectByte((byte)'R');

        // Expect: n=1 (success), then 0x11, 0x22, 0x33
        uno.RunUntilSerialBytes(uno.Serial, after + 4, maxMs: 5000);
        var resp = uno.Serial.Bytes.Skip(after).Take(4).ToArray();
        resp[0].Should().Be(1, "readfrom_into returns 1 on success");
        resp[1].Should().Be(0x11);
        resp[2].Should().Be(0x22);
        resp[3].Should().Be(0x33);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ArduinoUnoSimulation Sim()
    {
        var uno = new ArduinoUnoSimulation();
        uno.WithHex(_hex);
        uno.AddTwi(AvrTwi.TwiConfig, out _);
        return uno;
    }

    private static ArduinoUnoSimulation SimWithRecorder(byte address,
        out RecordingI2cDevice recorder)
    {
        var uno = new ArduinoUnoSimulation();
        uno.WithHex(_hex);
        uno.AddTwi(AvrTwi.TwiConfig, out var twi);
        var dev = new RecordingI2cDevice(twi, address);
        twi.EventHandler = dev;
        recorder = dev;
        return uno;
    }

    private static ArduinoUnoSimulation SimWithSender(byte address, params byte[] data)
    {
        var uno = new ArduinoUnoSimulation();
        uno.WithHex(_hex);
        uno.AddTwi(AvrTwi.TwiConfig, out var twi);
        twi.EventHandler = new SendingI2cDevice(twi, address, data);
        return uno;
    }

    /// <summary>ACKs a specific address and records bytes written to it.</summary>
    private sealed class RecordingI2cDevice(AvrTwi twi, byte address) : ITwiEventHandler
    {
        public List<byte> ReceivedBytes { get; } = [];

        public void Start(bool repeated) => twi.CompleteStart();
        public void Stop() => twi.CompleteStop();

        public void ConnectToSlave(byte addr, bool write) =>
            twi.CompleteConnect(addr == address);

        public void WriteByte(byte data)
        {
            ReceivedBytes.Add(data);
            twi.CompleteWrite(true);
        }

        public void ReadByte(bool ack) => twi.CompleteRead(0xFF);
    }

    /// <summary>ACKs a specific address and streams a fixed byte sequence on read.</summary>
    private sealed class SendingI2cDevice(AvrTwi twi, byte address, byte[] data) : ITwiEventHandler
    {
        private int _idx;

        public void Start(bool repeated)
        {
            _idx = 0;
            twi.CompleteStart();
        }

        public void Stop() => twi.CompleteStop();

        public void ConnectToSlave(byte addr, bool write) =>
            twi.CompleteConnect(addr == address);

        public void WriteByte(byte d) => twi.CompleteWrite(true);

        public void ReadByte(bool ack)
        {
            byte b = _idx < data.Length ? data[_idx++] : (byte)0xFF;
            twi.CompleteRead(b);
        }
    }
}
