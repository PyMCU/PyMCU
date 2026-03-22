using AVR8Sharp.Core.Peripherals;
using Avr8Sharp.TestKit.Boards;

namespace Whisnake.IntegrationTests.Tests.AVR;

/// <summary>
/// Simulates a DHT11 sensor on a single GPIO pin.
///
/// Protocol injected (all timing in simulated wall-clock milliseconds):
///   1. Detects the firmware's 18 ms start pulse by waiting until the pin
///      enters InputPullup state (DDR=0, PORT=1 — the exact result of
///      high() followed by mode(Pin.IN)).
///   2. ACK LOW  — drives pin LOW for 80 µs, then releases HIGH.
///   3. ACK HIGH — holds HIGH for 80 µs, then drives LOW.
///   4. 40 data bits MSB-first: pre-bit LOW 50 µs,
///      then HIGH 26 µs ('0') or 70 µs ('1').
///
/// The firmware threshold is <c>duration &gt; 40</c> loop-iterations.
/// At ~14 cycles/iteration on 16 MHz the effective boundary is ~36 µs:
///   '0' → 26 µs → ~29 iterations ≤ 40  ✓
///   '1' → 70 µs → ~77 iterations > 40  ✓
/// </summary>
internal sealed class Dht11Simulator
{
    private readonly ArduinoUnoSimulation _sim;
    private readonly AvrIoPort _port;
    private readonly byte _bit;

    /// <param name="sim">Simulation that has already received the boot banner.</param>
    /// <param name="port">Port the DHT11 data line belongs to (e.g. <c>PortD</c>).</param>
    /// <param name="bit">Bit index within that port (e.g. <c>2</c> for PD2).</param>
    public Dht11Simulator(ArduinoUnoSimulation sim, AvrIoPort port, byte bit)
    {
        _sim  = sim;
        _port = port;
        _bit  = bit;
    }

    /// <summary>Injects a well-formed DHT11 response with a correct checksum.</summary>
    public void Respond(byte humidity, byte temperature)
    {
        byte checksum = (byte)((humidity + temperature) & 0xFF);
        RespondRaw(humidity, 0, temperature, 0, checksum);
    }

    /// <summary>Injects a response with a deliberately wrong checksum.</summary>
    public void RespondWithBadChecksum(byte humidity, byte temperature)
    {
        byte badChecksum = (byte)((humidity + temperature + 1) & 0xFF);
        RespondRaw(humidity, 0, temperature, 0, badChecksum);
    }

    // ── Core protocol ─────────────────────────────────────────────────────────

    private void RespondRaw(
        byte humInt, byte humDec, byte tempInt, byte tempDec, byte checksum)
    {
        // Line starts HIGH (simulates the pull-up resistor on the data line).
        _port.SetPinValue(_bit, true);

        // Wait for the firmware start signal to end.
        // The firmware does: mode(OUT) → low() → delay_ms(18) → high() → delay_us(30) → mode(IN).
        // After high() + mode(IN): DDR=0, PORT=1 → GetPinState returns InputPullup.
        // At that point the firmware is about to enter pulse_in(0, 1000).
        WaitForStartSignalEnd();

        // ACK LOW (~80 µs) ────────────────────────────────────────────────────
        // Firmware is in pulse_in(0) wait loop (waiting for LOW).
        _port.SetPinValue(_bit, false);   // drive LOW → firmware breaks wait loop
        _sim.RunMilliseconds(0.08);       // hold 80 µs (firmware measures ACK-low duration)
        _port.SetPinValue(_bit, true);    // release HIGH → firmware breaks measure loop

        // ACK HIGH (~80 µs) ───────────────────────────────────────────────────
        // Pin is HIGH; firmware's pulse_in(1) wait loop immediately breaks,
        // then counts how long the HIGH lasts.
        _sim.RunMilliseconds(0.08);
        _port.SetPinValue(_bit, false);   // end ACK HIGH, begin data stream

        // 40 data bits (MSB first per byte) ───────────────────────────────────
        foreach (var b in new[] { humInt, humDec, tempInt, tempDec, checksum })
            SendByte(b);
    }

    /// <summary>
    /// Runs the sim until the data pin is in InputPullup state (DDR=0, PORT=1),
    /// which is the exact state after the firmware executes high() then mode(Pin.IN).
    /// If the pin is already in that state (Boot returned after mode(IN) ran),
    /// returns immediately without advancing the simulation.
    /// </summary>
    private void WaitForStartSignalEnd()
    {
        if (_port.GetPinState(_bit) == PinState.InputPullup)
            return;

        _sim.RunUntilMs(_ => _port.GetPinState(_bit) == PinState.InputPullup, maxMs: 30);
    }

    /// <summary>Clocks out one byte MSB-first using DHT11 pulse-width encoding.</summary>
    private void SendByte(byte value)
    {
        for (int i = 7; i >= 0; i--)
        {
            // Pre-bit LOW: firmware's pulse_in(1) wait loop spins here.
            _sim.RunMilliseconds(0.05);          // 50 µs LOW

            _port.SetPinValue(_bit, true);       // drive HIGH → firmware breaks wait loop

            bool one = ((value >> i) & 1) == 1;
            _sim.RunMilliseconds(one ? 0.070 : 0.026); // '1' = 70 µs, '0' = 26 µs

            _port.SetPinValue(_bit, false);      // drive LOW → firmware breaks measure loop
        }
    }
}
