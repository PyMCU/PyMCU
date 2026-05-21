# SPI — `pymcu.hal.spi`

```python
from pymcu.hal.spi import SPI
```

SPI bus communication in master mode. Available for AVR (ATmega328P).

---

## class `SPI`

### `SPI(cs: str = "PB2")`

Initializes the hardware SPI peripheral in master mode, Mode 0, MSB-first, at fosc/4
(4 MHz at 16 MHz clock). `cs` is the chip-select pin, auto-asserted on `select()`.

**Pinout (ATmega328P / Arduino Uno):**

| Pin | Arduino | Function |
|---|---|---|
| `PB5` | D13 | SCK |
| `PB4` | D12 | MISO |
| `PB3` | D11 | MOSI |
| `PB2` | D10 | SS / CS |

### Methods

| Method | Description |
|---|---|
| `select()` | Drive SS low (begin transaction) |
| `deselect()` | Drive SS high (end transaction) |
| `transfer(data: uint8) -> uint8` | Full-duplex byte exchange |
| `write(data: uint8)` | Send byte (discard received byte) |
| `__enter__()` | Alias for `select()` — called by `with spi:` |
| `__exit__()` | Alias for `deselect()` — called by `with spi:` |

---

## Examples

### Context manager (recommended)

```python
from pymcu.hal.spi import SPI

spi = SPI()

with spi:
    spi.write(0xAB)
    b = spi.transfer(0x00)
```

`with spi:` calls `select()` on enter and `deselect()` on exit, even if the block returns early.

### 74HC595 shift register

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

### SoftSPI (bit-bang)

Use `SoftSPI` for arbitrary GPIO pins (no hardware SPI constraint):

```python
from pymcu.hal.spi import SoftSPI
from pymcu.types import uint8

spi = SoftSPI(sck="PD4", mosi="PD5", miso="PD6", cs="PD7")

with spi:
    b: uint8 = spi.transfer(0x55)
```
