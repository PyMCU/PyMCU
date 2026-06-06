using FluentAssertions;
using NUnit.Framework;
using Avr8Sharp.TestKit.Boards;
using Avr8Sharp.TestKit;
using AVR8Sharp.Core.Peripherals;

namespace PyMCU.IntegrationTests.Tests.AVR;

/// <summary>
/// Integration tests for fixtures/avr/compat-mp-spi-rw.
/// Verifies machine.SPI multi-byte deviations from MicroPython:
///
///   write(buf, n)                   deviation: MicroPython write(buf) infers len
///   readinto(buf, n, write_byte)    deviation: MicroPython readinto(buf) fills len(buf)
///   write_readinto(w_buf, r_buf, n) deviation: MicroPython infers len from buffers
///
/// PyMCU always requires an explicit byte count because fixed-size arrays
/// have no runtime len().
/// </summary>
[TestFixture]
public class CompatMpSpiRwTests
{
    private static string _hex = null!;

    [OneTimeSetUp]
    public void BuildFirmware() =>
        _hex = PymcuCompiler.BuildFixture("compat-mp-spi-rw");

    [Test]
    public void Boot_SendsReadyBanner()
    {
        var uno = Sim();
        uno.RunUntilSerial(uno.Serial, "READY", maxMs: 300);
        uno.Serial.Should().Contain("READY");
    }

    // ── write(buf, n) ─────────────────────────────────────────────────────────

    [Test]
    public void Write_SendsThreeBytes_InOrder()
    {
        // Capture every byte the firmware transmits via SPI.
        var sent = new List<byte>();
        var uno = SimWithCapture(b => { sent.Add(b); return 0; });

        uno.RunUntilSerial(uno.Serial, "READY\n", maxMs: 300);
        var after = uno.Serial.ByteCount;

        uno.Serial.InjectByte((byte)'W');

        // Firmware echoes 'W' when write() completes.
        uno.RunUntilSerialBytes(uno.Serial, after + 1, maxMs: 1000);
        uno.Serial.Bytes[after].Should().Be((byte)'W');
        sent.Should().Equal(new byte[] { 0xAA, 0xBB, 0xCC },
            "three bytes transmitted in buffer order");
    }

    // ── readinto(buf, n) ──────────────────────────────────────────────────────

    [Test]
    public void Readinto_ReceivesThreeBytes_FromPeripheral()
    {
        // Peripheral returns 0x11, 0x22, 0x33 for each MOSI dummy byte.
        int idx = 0;
        byte[] rxData = [0x11, 0x22, 0x33];
        var uno = SimWithCapture(_ => idx < rxData.Length ? rxData[idx++] : (int)0xFF);

        uno.RunUntilSerial(uno.Serial, "READY\n", maxMs: 300);
        var after = uno.Serial.ByteCount;

        uno.Serial.InjectByte((byte)'R');

        // Firmware echoes the 3 received bytes over UART.
        uno.RunUntilSerialBytes(uno.Serial, after + 3, maxMs: 1000);
        var resp = uno.Serial.Bytes.Skip(after).Take(3).ToArray();
        resp[0].Should().Be(0x11, "in_buf[0] = first received byte");
        resp[1].Should().Be(0x22, "in_buf[1] = second received byte");
        resp[2].Should().Be(0x33, "in_buf[2] = third received byte");
    }

    [Test]
    public void Readinto_SendsDummyByte_0xFF_ByDefault()
    {
        // Default write_byte for readinto is 0xFF; verify MOSI line.
        var mosiBytes = new List<byte>();
        var uno = SimWithCapture(b => { mosiBytes.Add(b); return 0; });

        uno.RunUntilSerial(uno.Serial, "READY\n", maxMs: 300);
        var after = uno.Serial.ByteCount;

        uno.Serial.InjectByte((byte)'R');

        uno.RunUntilSerialBytes(uno.Serial, after + 3, maxMs: 1000);
        mosiBytes.Should().Equal(new byte[] { 0xFF, 0xFF, 0xFF },
            "readinto clocks 0xFF as dummy output by default");
    }

    // ── write_readinto(w_buf, r_buf, n) ───────────────────────────────────────

    [Test]
    public void WriteReadinto_FullDuplex_ThreeBytes()
    {
        // Peripheral echoes each byte + 1 (0xAA→0xAB, 0xBB→0xBC, 0xCC→0xCD).
        var sent = new List<byte>();
        var uno = SimWithCapture(b => { sent.Add(b); return b + 1; });

        uno.RunUntilSerial(uno.Serial, "READY\n", maxMs: 300);
        var after = uno.Serial.ByteCount;

        uno.Serial.InjectByte((byte)'X');

        // Firmware echoes 'X' then 3 in_buf bytes.
        uno.RunUntilSerialBytes(uno.Serial, after + 4, maxMs: 1000);
        var resp = uno.Serial.Bytes.Skip(after).Take(4).ToArray();
        resp[0].Should().Be((byte)'X', "completion marker");
        resp[1].Should().Be(0xAB, "in_buf[0] = 0xAA+1");
        resp[2].Should().Be(0xBC, "in_buf[1] = 0xBB+1");
        resp[3].Should().Be(0xCD, "in_buf[2] = 0xCC+1");
        sent.Should().Equal(new byte[] { 0xAA, 0xBB, 0xCC },
            "out_buf transmitted in order");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ArduinoUnoSimulation Sim()
    {
        var uno = new ArduinoUnoSimulation();
        uno.WithHex(_hex);
        uno.AddSpi(AvrSpi.SpiConfig, out _);
        return uno;
    }

    /// <summary>
    /// Creates a simulation whose SPI peripheral calls <paramref name="onTransfer"/>
    /// for every byte transmitted. The function receives the transmitted byte and
    /// returns the byte to be received (MISO).
    /// </summary>
    private static ArduinoUnoSimulation SimWithCapture(Func<byte, int> onTransfer)
    {
        var uno = new ArduinoUnoSimulation();
        uno.WithHex(_hex);
        uno.AddSpi(AvrSpi.SpiConfig, out var spi);
        spi.OnTransfer = onTransfer;
        return uno;
    }
}
