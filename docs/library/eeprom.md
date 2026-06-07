# EEPROM — `pymcu.hal.eeprom`

```python
from pymcu.hal.eeprom import EEPROM
```

Non-volatile byte storage using the internal EEPROM. `EEPROM` is a zero-cost `@inline` class —
all reads and writes compile to bare register-level sequences with no SRAM overhead.

---

## Capacity by chip

| Chip | Size | Address range | Write latency |
|---|---|---|---|
| ATmega328P | 1024 bytes | `0x000`–`0x3FF` | ~3.4 ms |
| ATmega2560 | 4096 bytes | `0x000`–`0xFFF` | ~3.4 ms |
| ATmega32U4 | 1024 bytes | `0x000`–`0x3FF` | ~3.4 ms |
| ATtiny85/45/25 | 512/256/128 bytes | varies | ~3.4 ms |

Endurance: **100 000 write/erase cycles** per cell. Data retention: **20+ years** at 25 °C.

---

## class `EEPROM`

### `EEPROM()`

Creates an EEPROM handle. No registers are written at construction time.

### Methods

| Method | Signature | Description |
|---|---|---|
| `write` | `write(addr: uint16, value: uint8)` | Write one byte. Blocks ~3.4 ms for write to complete. |
| `read` | `read(addr: uint16) -> uint8` | Read one byte. Returns immediately. |

---

## Example

```python
from pymcu.hal.eeprom import EEPROM
from pymcu.types import uint8, uint16

def main():
    ee = EEPROM()

    # Persist a calibration offset
    ee.write(0x00, 42)

    # Read it back after power cycle
    cal: uint8 = ee.read(0x00)
```

---

## Wear-leveling

Each cell has a finite write endurance (~100 k cycles). For values that change frequently
(counters, log entries), distribute writes across multiple addresses rather than hitting the
same cell repeatedly.

---

## ATmega328P register map

| Register | Address | Description |
|---|---|---|
| `EEARH` | `0x42` | EEPROM Address High |
| `EEARL` | `0x41` | EEPROM Address Low |
| `EEDR` | `0x40` | EEPROM Data Register |
| `EECR` | `0x3F` | EEPROM Control Register (EEPE, EEMPE, EERIE, EEPM) |
