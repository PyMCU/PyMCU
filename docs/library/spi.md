# SPI — `pymcu.hal.spi`

```python
from pymcu.hal.spi import SPI
```

SPI bus communication in master mode. Available for AVR (ATmega328P).

---

## class `SPI`

### `SPI(mode=0, cs="", baudrate=4000000, polarity=0, phase=0, lsb_first=0)`

Initializes the hardware SPI peripheral. `mode` is `0` for controller and `1` for peripheral;
`cs` is an optional chip-select pin, auto-asserted on `select()`.

`baudrate`, `polarity`, `phase` and `lsb_first` are the clock and the frame. `polarity` and
`phase` are the two bits that name the SPI mode (mode 0 is `0, 0`; mode 3 is `1, 1`), and
anything but 0 or 1 for either is refused where the bus is constructed. The defaults are what
the literal control-register value used to be: mode 0, MSB first, fosc/4, which is 4 MHz at a
16 MHz clock, so a bus that asks for nothing emits the same two register writes it always did.

They used to be nowhere, so `busio.SPI.configure()` recorded a baudrate, a polarity and a
phase and reprogrammed none of them: a display asked for mode 3 at 8 MHz ran mode 0 at 4 MHz,
silently.

`configure(...)` reprograms a bus that is already running; the pin directions are already set,
so only the two control registers are written.

`frequency()` returns the bit rate the hardware actually produces. The dividers are powers of
two -- 2, 4, 8, 16, 32, 64, 128 -- and the chosen one never **exceeds** the request, because a
peripheral rated for 1 MHz must not be clocked at 2. Asking for 3 MHz at a 16 MHz clock gets
2 MHz, and reporting 3 MHz back would be a number the pin never carried.

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
from pymcu.hal.softspi import SoftSPI
from pymcu.hal.avr.gpio import Pin
from pymcu.types import uint8

spi = SoftSPI(sck=Pin("PD4", Pin.OUT), mosi=Pin("PD5", Pin.OUT),
              miso=Pin("PD6", Pin.IN), cs=Pin("PD7", Pin.OUT))

with spi:
    b: uint8 = spi.transfer(0x55)
```
