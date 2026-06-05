# eeprom

Write and read back values from the on-chip EEPROM.

## What it does

Writes a known 4-byte pattern (`0xA1 0xB2 0xC3 0xD4`) to EEPROM addresses 0–3,
reads them back, and prints `EEPROM OK` if every value matches or `EEPROM FAIL`
otherwise.

## Hardware

- Arduino Uno / any ATmega328P board @ 16 MHz
- Serial terminal at **9600 baud** (no external wiring)

## Expected output

```
EEPROM TEST
EEPROM OK
```

## Key concepts

- `pymcu.hal.eeprom.EEPROM` with `write(addr, val)` / `read(addr)`
- Non-volatile storage that survives power cycles

## Build & flash

```bash
cd examples/avr/eeprom
pymcu build
pymcu flash --port /dev/cu.usbmodemXXXX
```
