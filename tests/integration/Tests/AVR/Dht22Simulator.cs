using AVR8Sharp.Core.Peripherals;
using Avr8Sharp.TestKit.Boards;

namespace PyMCU.IntegrationTests.Tests.AVR;

/// <summary>
/// Simulates a DHT22 sensor on a single GPIO pin.
///
/// DHT22 differs from DHT11 only in data encoding:
///   - Humidity: 16-bit fixed-point, value = raw / 10.0
///   - Temperature: 16-bit fixed-point; bit 15 of raw = sign bit; value = (raw &amp; 0x7FFF) / 10.0
///   - Each byte: humInt, humDec, tempInt, tempDec, checksum
///
/// The wire protocol (start pulse, ACK, 40-bit stream) is identical to DHT11.
/// </summary>
internal sealed class Dht22Simulator
{
    private readonly ArduinoUnoSimulation _sim;
    private readonly AvrIoPort _port;
    private readonly byte _bit;

    /// <param name="sim">Simulation that has already received the boot banner.</param>
    /// <param name="port">Port the DHT22 data line belongs to (e.g. <c>PortD</c>).</param>
    /// <param name="bit">Bit index within that port (e.g. <c>2</c> for PD2).</param>
    public Dht22Simulator(ArduinoUnoSimulation sim, AvrIoPort port, byte bit)
    {
        _sim  = sim;
        _port = port;
        _bit  = bit;
    }

    /// <summary>
    /// Injects a well-formed DHT22 response with a correct checksum.
    /// </summary>
    /// <param name="humidityTenths">Humidity × 10 (e.g. 550 for 55.0 %).</param>
    /// <param name="temperatureTenths">Temperature × 10, signed (e.g. 235 for 23.5 °C, -50 for -5.0 °C).</param>
    public void Respond(int humidityTenths, int temperatureTenths)
    {
        byte humInt  = (byte)((humidityTenths >> 8) & 0xFF);
        byte humDec  = (byte)(humidityTenths & 0xFF);

        bool negative  = temperatureTenths < 0;
        int  absTenths = negative ? -temperatureTenths : temperatureTenths;
        byte tempInt   = (byte)(((absTenths >> 8) & 0x7F) | (negative ? 0x80 : 0));
        byte tempDec   = (byte)(absTenths & 0xFF);

        byte checksum = (byte)((humInt + humDec + tempInt + tempDec) & 0xFF);
        RespondRaw(humInt, humDec, tempInt, tempDec, checksum);
    }

    /// <summary>Injects a response with a deliberately wrong checksum.</summary>
    public void RespondWithBadChecksum(int humidityTenths, int temperatureTenths)
    {
        byte humInt  = (byte)((humidityTenths >> 8) & 0xFF);
        byte humDec  = (byte)(humidityTenths & 0xFF);

        bool negative  = temperatureTenths < 0;
        int  absTenths = negative ? -temperatureTenths : temperatureTenths;
        byte tempInt   = (byte)(((absTenths >> 8) & 0x7F) | (negative ? 0x80 : 0));
        byte tempDec   = (byte)(absTenths & 0xFF);

        byte badChecksum = (byte)((humInt + humDec + tempInt + tempDec + 1) & 0xFF);
        RespondRaw(humInt, humDec, tempInt, tempDec, badChecksum);
    }

    // ── Core protocol (identical to Dht11Simulator) ───────────────────────────

    private void RespondRaw(
        byte humInt, byte humDec, byte tempInt, byte tempDec, byte checksum)
    {
        _port.SetPinValue(_bit, true);

        WaitForStartSignalEnd();

        // ACK LOW (~80 µs)
        _port.SetPinValue(_bit, false);
        _sim.RunMilliseconds(0.08);
        _port.SetPinValue(_bit, true);

        // ACK HIGH (~80 µs)
        _sim.RunMilliseconds(0.08);
        _port.SetPinValue(_bit, false);

        // 40 data bits (MSB first per byte)
        foreach (var b in new[] { humInt, humDec, tempInt, tempDec, checksum })
            SendByte(b);
    }

    private void WaitForStartSignalEnd()
    {
        // Runs the sim until the data pin is in InputPullup state (DDR=0, PORT=1),
        // which is the exact state after the firmware executes high() then mode(Pin.IN).
        // If the pin is already in that state (Boot returned after mode(IN) ran),
        // returns immediately without advancing the simulation.
        if (_port.GetPinState(_bit) == PinState.InputPullup)
            return;

        _sim.RunUntilMs(_ => _port.GetPinState(_bit) == PinState.InputPullup, maxMs: 30);
    }

    private void SendByte(byte value)
    {
        for (int i = 7; i >= 0; i--)
        {
            _sim.RunMilliseconds(0.05);

            _port.SetPinValue(_bit, true);

            bool one = ((value >> i) & 1) == 1;
            _sim.RunMilliseconds(one ? 0.070 : 0.026);

            _port.SetPinValue(_bit, false);
        }
    }
}
