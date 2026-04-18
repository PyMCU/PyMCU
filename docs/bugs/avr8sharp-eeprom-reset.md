# Bug: AvrEeprom write-timing counters not reset by Cpu.Reset()

**Project:** AVR8Sharp / Avr8Sharp.TestKit  
**Affected version:** 1.1.0-beta1  
**Severity:** High (causes silent firmware hang in test environments that reuse a simulation)

---

## Summary

`AvrEeprom` keeps two internal cycle-count fields — `_writeCompleteCycles` and
`_writeEnabledCycles` — that are updated each time an EEPROM write is initiated.
These fields are **not cleared** when `Cpu.Reset()` is called, nor when `Cpu.Cycles`
is reset to 0.

As a result, reusing an `ArduinoUnoSimulation` across multiple test runs (by resetting
the CPU rather than constructing a new object) causes EEPROM writes in subsequent runs
to malfunction silently.

---

## Steps to reproduce

1. Create an `ArduinoUnoSimulation`, load firmware that performs an EEPROM write, and run
   it to completion.  `_writeCompleteCycles` is now set to some value N > 0.
2. Call `Cpu.Reset()` and set `Cpu.Cycles = 0`.
3. Run the same firmware again from the start.
4. The firmware executes `OUT EECR, EEMPE` followed by `SBI EECR, EEPE` to start a write.
5. The `RegisterWrite` callback in `AvrEeprom` evaluates:

   ```csharp
   if (cpu.Cycles < avrEeprom._writeCompleteCycles)
       return true;   // write skipped
   ```

   Because `Cpu.Cycles` was reset to 0 but `_writeCompleteCycles` is still N, the
   condition is true and the write is silently skipped.
6. EEPE is left set in EECR but **no clock event is scheduled** to clear it.
7. The firmware's busy-wait loop `while (EECR & EEPE)` spins forever.

---

## Expected behaviour

`Cpu.Reset()` should reset all peripherals attached to the CPU to their power-on state,
including `AvrEeprom`.  At minimum, `AvrEeprom` should expose a `Reset()` method
(matching the pattern of `AvrTimer`) that clears `_writeCompleteCycles` and
`_writeEnabledCycles`, and `Cpu.Reset()` should invoke it.

---

## Actual behaviour

`_writeCompleteCycles` and `_writeEnabledCycles` retain their end-of-previous-run values
after `Cpu.Reset()`.  Any firmware that initiates an EEPROM write immediately after a
CPU reset (i.e. before `Cpu.Cycles` has advanced past the stale counter value) will hang.

---

## Workaround

Construct a new `ArduinoUnoSimulation` for each test run instead of resetting a shared
instance.  The compiled HEX can still be cached separately so it is only parsed once:

```csharp
private string _hex = null!;

[OneTimeSetUp]
public void BuildFirmware() => _hex = PymcuCompiler.Build("eeprom");

private ArduinoUnoSimulation Sim()
{
    var uno = new ArduinoUnoSimulation();
    uno.WithHex(_hex);
    return uno;
}
```

This is the approach used in `tests/integration/Tests/AVR/EepromTests.cs` in the PyMCU
project while the upstream bug is unresolved.

---

## Suggested fix (AVR8Sharp)

Add a `Reset()` method to `AvrEeprom` and call it from `Cpu.Reset()`:

```csharp
// In AvrEeprom
public void Reset()
{
    _writeEnabledCycles  = 0u;
    _writeCompleteCycles = 0u;
}
```

```csharp
// In Cpu.Reset() — after clearing clock events
foreach (var peripheral in _peripherals)
    peripheral.Reset();
```

Alternatively, `Cpu.Reset()` could broadcast a reset signal to all registered MMIO
write handlers so each peripheral can clean up its own state.
