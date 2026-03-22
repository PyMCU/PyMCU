# EEPROM — `whisnake.hal.eeprom`

```python
from whisnake.hal.eeprom import EEPROM
```

Non-volatile byte storage using the internal EEPROM. No heap allocation — the `EEPROM` class is a
zero-cost `@inline` wrapper; all reads and writes compile down to the bare register-level sequence.

## ATmega328P capacity

| Property | Value |
|---|---|
| Size | 1024 bytes |
| Address range | `0x000` – `0x3FF` |
| Write latency | ~3.4 ms (hardware-timed, polled) |
| Endurance | 100,000 write/erase cycles |
| Data retention | 20+ years at 25 °C |

## `EEPROM()`

Creates an EEPROM handle. No registers are written at construction time.

### Methods

| Method | Signature | Description |
|---|---|---|
| `write` | `write(addr: uint16, value: uint8)` | Write one byte to address `addr`. Blocks until any prior write completes (~3.4 ms). |
| `read` | `read(addr: uint16) -> uint8` | Read one byte from address `addr`. Returns immediately. |

## Usage

```python
from whisnake.hal.eeprom import EEPROM
from whisnake.types import uint8, uint16

def main():
    ee = EEPROM()

    # Persist a calibration value
    ee.write(0x00, 42)

    # Read it back (survives power cycle)
    cal: uint8 = ee.read(0x00)
```

## Wear-leveling note

Each address has a finite write endurance (~100 k cycles).  For values that change frequently
(counters, log entries), distribute writes across multiple addresses rather than hitting the
same cell repeatedly.

## ATmega328P register map

| Register | Address | Description |
|---|---|---|
| `EEARH` | `0x42` | EEPROM Address Register High |
| `EEARL` | `0x41` | EEPROM Address Register Low |
| `EEDR` | `0x40` | EEPROM Data Register |
| `EECR` | `0x3F` | EEPROM Control Register (EEPE, EEMPE, EERIE, EEPM) |
