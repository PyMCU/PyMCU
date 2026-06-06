using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;
using AVR8Sharp.Core.Peripherals;

namespace PyMCU.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for fixtures/avr/compat-mp-i2c-scan.
/// Verifies machine.I2C.scan(buf, max_count) from the pymcu-micropython compat layer.
///
/// Deviation from MicroPython:
///   - MicroPython: i2c.scan() -> list[int]  (heap allocated, variable length)
///   - PyMCU:       i2c.scan(buf, max_count) -> uint8 count; caller provides buffer
///
/// The no-arg overload i2c.scan() still exists for backward compat and returns
/// only the device count (no addresses stored).
/// </summary>
[TestFixture]
public class CompatMpI2cScanTests
{
    private static string _hex = null!;

    [OneTimeSetUp]
    public void BuildFirmware() =>
        _hex = PymcuCompiler.BuildFixture("compat-mp-i2c-scan");

    [Test]
    public void Boot_SendsReadyBanner()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "READY", maxMs: 300);
        uno.Serial.Should().Contain("READY");
    }

    [Test]
    public void ScanWithBuf_NoDevices_ReturnsCountZero()
    {
        // Default TWI handler NACKs all addresses -- count must be 0.
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "READY\n", maxMs: 300);
        var after = uno.Serial.ByteCount;

        uno.Serial.InjectByte((byte)'S');

        // Full 127-address scan is slow in simulation.
        uno.RunUntilSerialBytes(uno.Serial, after + 1, maxMs: 20000);
        uno.Serial.Bytes[after].Should().Be(0, "no devices on bus");
    }

    [Test]
    public void ScanWithBuf_OneDevice_StoresAddressInBuffer()
    {
        // Stub device ACKs at address 0x3C (e.g. SSD1306 OLED default address).
        var uno = SimWithDevices(0x3C);
        uno.RunUntilSerial(uno.Serial, "READY\n", maxMs: 300);
        var after = uno.Serial.ByteCount;

        uno.Serial.InjectByte((byte)'S');

        // Expect: count=1, then address byte 0x3C
        uno.RunUntilSerialBytes(uno.Serial, after + 2, maxMs: 20000);
        var resp = uno.Serial.Bytes.Skip(after).Take(2).ToArray();
        resp[0].Should().Be(1, "one device found");
        resp[1].Should().Be(0x3C, "address 0x3C stored in buf[0]");
    }

    [Test]
    public void ScanWithBuf_TwoDevices_StoresBothAddressesAscending()
    {
        // Stub devices at 0x3C (SSD1306) and 0x48 (ADS1115).
        var uno = SimWithDevices(0x3C, 0x48);
        uno.RunUntilSerial(uno.Serial, "READY\n", maxMs: 300);
        var after = uno.Serial.ByteCount;

        uno.Serial.InjectByte((byte)'S');

        // Expect: count=2, then 0x3C (lower address first -- scan is linear 1->127)
        uno.RunUntilSerialBytes(uno.Serial, after + 3, maxMs: 20000);
        var resp = uno.Serial.Bytes.Skip(after).Take(3).ToArray();
        resp[0].Should().Be(2, "two devices found");
        resp[1].Should().Be(0x3C, "buf[0] = 0x3C (lower address first)");
        resp[2].Should().Be(0x48, "buf[1] = 0x48");
    }

    [Test]
    public void ScanCountOnly_BackwardCompat_NoDevices()
    {
        // i2c.scan() (no-arg overload) still returns count, no buffer.
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "READY\n", maxMs: 300);
        var after = uno.Serial.ByteCount;

        uno.Serial.InjectByte((byte)'C');

        uno.RunUntilSerialBytes(uno.Serial, after + 1, maxMs: 20000);
        uno.Serial.Bytes[after].Should().Be(0, "no devices on empty bus");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ArduinoUnoSimulation Sim()
    {
        var uno = new ArduinoUnoSimulation();
        uno.WithHex(_hex);
        uno.AddTwi(AvrTwi.TwiConfig, out _);
        return uno;
    }

    private static ArduinoUnoSimulation SimWithDevices(params byte[] addresses)
    {
        var uno = new ArduinoUnoSimulation();
        uno.WithHex(_hex);
        uno.AddTwi(AvrTwi.TwiConfig, out var twi);
        twi.EventHandler = new StubI2cDevices(twi, addresses);
        return uno;
    }

    /// <summary>ACKs a fixed set of 7-bit addresses; NACKs everything else.</summary>
    private sealed class StubI2cDevices(AvrTwi twi, byte[] addresses) : ITwiEventHandler
    {
        public void Start(bool repeated) => twi.CompleteStart();
        public void Stop() => twi.CompleteStop();

        public void ConnectToSlave(byte address, bool write) =>
            twi.CompleteConnect(addresses.Contains(address));

        public void WriteByte(byte data) => twi.CompleteWrite(true);
        public void ReadByte(bool ack) => twi.CompleteRead(0xFF);
    }
}
