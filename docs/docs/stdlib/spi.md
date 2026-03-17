# SPI — `pymcu.hal.spi`

```python
from pymcu.hal.spi import SPI
```

SPI support is currently available for AVR (ATmega328P) only.

## `SPI()`

Initializes the hardware SPI peripheral in master mode, Mode 0, MSB-first, at fosc/4 (4 MHz at
16 MHz clock).

**Pinout (ATmega328P / Arduino Uno):**

| Pin | Arduino | Function |
|---|---|---|
| `PB5` | D13 | SCK |
| `PB4` | D12 | MISO |
| `PB3` | D11 | MOSI |
| `PB2` | D10 | SS (manual control) |

### Methods

| Method | Description |
|---|---|
| `select()` | Drive SS low (begin transaction) |
| `deselect()` | Drive SS high (end transaction) |
| `transfer(data: uint8) -> uint8` | Full-duplex byte exchange |
| `write(data: uint8)` | Send byte (ignore received byte) |
| `__enter__()` | Alias for `select()` — called by `with spi:` |
| `__exit__()` | Alias for `deselect()` — called by `with spi:` |

## Context manager (`with` statement)

Use `with spi:` to automatically select and deselect the device around a transaction:

```python
with spi:
    spi.write(0xAB)           # select() called before, deselect() called after
```

This is the preferred pattern — it guarantees `deselect()` is always called even if an early
`return` is taken from inside the block.

## Example — 74HC595 shift register

```python
from pymcu.hal.spi import SPI
from pymcu.time import delay_ms
from pymcu.types import uint8

def main():
    spi = SPI()

    data: uint8 = 0x01
    while True:
        with spi:
            spi.write(data)
        data = (data << 1) | (data >> 7)   # rotate left
        delay_ms(100)
```
