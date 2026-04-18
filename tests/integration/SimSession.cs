using System.Reflection;
using Avr8Sharp.TestKit.Boards;

namespace PyMCU.IntegrationTests;

/// <summary>
/// Manages a shared <see cref="ArduinoUnoSimulation"/> for a single test fixture.
///
/// The simulation is created once per fixture (in <c>[OneTimeSetUp]</c>) with the
/// firmware HEX already loaded into program flash.  Every call to <see cref="Reset"/>
/// restores the simulation to its exact power-on state so each test starts from a
/// clean slate without the overhead of allocating a new simulation object or
/// re-parsing the HEX file.
/// </summary>
/// <remarks>
/// <para><b>Reset procedure</b></para>
/// <list type="bullet">
///   <item>Restore the <c>Data</c> array (CPU registers, I/O registers, SRAM) from a
///         snapshot captured immediately after construction — before any firmware has
///         run — preserving the correct power-on values set by peripheral constructors
///         (e.g. <c>UCSRA.UDRE = 1</c> set by the <c>AvrUsart</c> constructor).</item>
///   <item>Call <c>Timer0/1/2.Reset()</c> to clear internal timer counters and
///         prescaler-divider state.</item>
///   <item>Call <c>Cpu.Reset()</c> to set PC=0, SP=top-of-SRAM, SREG=0, and clear
///         all pending interrupts and scheduled clock events (so stale timer/UART
///         callbacks from the previous test cannot fire into the next test).</item>
///   <item>Reset <c>Cpu.Cycles</c> to 0 so <c>RunMilliseconds</c> measures from the
///         start of the new test.</item>
///   <item>Zero the EEPROM peripheral's internal write-timing counters
///         (<c>_writeCompleteCycles</c> / <c>_writeEnabledCycles</c>) so that a
///         stale value from the previous test cannot cause the EEPROM write
///         callback to silently skip the write and leave <c>EEPE</c> stuck high.</item>
///   <item>Clear the UART serial-probe receive buffer.</item>
/// </list>
/// </remarks>
public sealed class SimSession
{
    private static readonly FieldInfo? EepromWriteCompleteCycles =
        typeof(ArduinoUnoSimulation).Assembly
            .GetType("AVR8Sharp.Core.Peripherals.AvrEeprom")
            ?.GetField("_writeCompleteCycles", BindingFlags.NonPublic | BindingFlags.Instance);

    private static readonly FieldInfo? EepromWriteEnabledCycles =
        typeof(ArduinoUnoSimulation).Assembly
            .GetType("AVR8Sharp.Core.Peripherals.AvrEeprom")
            ?.GetField("_writeEnabledCycles", BindingFlags.NonPublic | BindingFlags.Instance);

    private readonly ArduinoUnoSimulation _sim;
    private readonly byte[] _dataSnapshot;

    /// <summary>
    /// Creates a session for the given firmware HEX content.
    /// </summary>
    /// <param name="hexContent">Intel HEX string returned by <see cref="PymcuCompiler"/>.</param>
    public SimSession(string hexContent)
    {
        _sim = new ArduinoUnoSimulation();

        // Snapshot taken BEFORE loading the HEX so it captures the peripheral
        // power-on defaults written by peripheral constructors (AvrUsart sets
        // UCSRA=32, UCSRC=6; Cpu.Reset sets SP=top, SREG=0).
        _dataSnapshot = (byte[])_sim.Data.Clone();

        _sim.WithHex(hexContent);
    }

    /// <summary>
    /// Resets the simulation to its power-on state and returns it, ready for a
    /// fresh test run.
    /// </summary>
    public ArduinoUnoSimulation Reset()
    {
        // 1. Restore all CPU registers, I/O registers, and SRAM to the initial state.
        Array.Copy(_dataSnapshot, _sim.Data, _dataSnapshot.Length);

        // 2. Reset timer internal counters, dividers, and OCR shadow registers.
        _sim.Timer0.Reset();
        _sim.Timer1.Reset();
        _sim.Timer2.Reset();

        // 3. Reset CPU: PC=0, SP=top, SREG=0, clear pending interrupts and all
        //    scheduled clock events (prevents stale peripheral callbacks carrying over).
        _sim.Cpu.Reset();

        // 4. Reset the cycle counter so RunMilliseconds(ms) measures from 0.
        _sim.Cpu.Cycles = 0;

        // 5. Reset EEPROM peripheral timing state.  Cpu.Reset() zeroes Cpu.Cycles but
        //    AvrEeprom keeps its own _writeCompleteCycles / _writeEnabledCycles counters.
        //    If a previous test left a stale _writeCompleteCycles > 0, the EEPROM write
        //    callback sees (Cycles=0) < _writeCompleteCycles and silently skips starting
        //    the new write while leaving EEPE=1 in EECR with no clock event to clear it —
        //    causing the firmware's polling loop to spin forever.
        if (_sim.Eeprom is not null)
        {
            EepromWriteCompleteCycles?.SetValue(_sim.Eeprom, 0u);
            EepromWriteEnabledCycles?.SetValue(_sim.Eeprom, 0u);
        }

        // 6. Clear the UART receive buffer captured by the serial probe.
        _sim.Serial.Clear();

        return _sim;
    }
}
