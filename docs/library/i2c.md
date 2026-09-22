# I2C — `pymcu.hal.i2c`

```python
from pymcu.hal.i2c import I2C
```

I2C (TWI) bus communication. Available for AVR (ATmega328P).

**Pinout (ATmega328P / Arduino Uno):**

| Signal | Pin | Arduino |
|---|---|---|
| SDA | `PC4` | A4 |
| SCL | `PC5` | A5 |

On the ATmega2560 and ATmega32U4 the bus is on `PD1` (SDA) and `PD0` (SCL) instead; the
chip module names the pair and the HAL picks it up, so the same source drives all three.

---

## class `I2C`

### `I2C(addr=0, general_call=0, freq=100000, pullups=True)`

Initializes the TWI peripheral. `addr = 0` is controller mode; a non-zero address makes the
part a peripheral at that address.

`pullups` switches the AVR's internal pull-ups on SDA and SCL, and defaults to on, the way
Arduino's `twi_init()` leaves them: a module with weak or missing pull-up resistors still
answers, where without any pull-up the lines float and the START condition never
completes. Twenty to fifty kOhm is enough for short runs at 100 kHz; long wires or 400 kHz
still want external resistors (the usual 4.7 kOhm). Pass `pullups=False` on a bus whose
devices are 3.3 V and should not see a 5 V pull-up -- the bus then needs external
resistors of its own. The flag is compile-time, so a program that never asks is identical.

The CircuitPython layer goes a step further, the way upstream does: `busio.I2C` reads SDA
and SCL right after the pull-ups come up and raises
`RuntimeError("No pull up found on SDA or SCL; check your wiring")` when either line sits
low -- the TWI does not drive the lines until the first START, so the read is the bus idle
level, and a line held low means something is pulling it down. `machine.I2C` does not
check; it raises `OSError` only when a transfer fails, and neither does `bitbangio.I2C`,
which drives its own pins.

`freq` is the SCL rate in Hz and reaches the bit-rate register:
`TWBR = (F_CPU / SCL - 16) / 2`, so 100 kHz at 16 MHz is 72 and 400 kHz is 12. It used to be
the literal 72 and nothing else, so every layer above took a frequency from its caller and
threw it away, and a bus asked for 400 kHz ran at a quarter of that with nothing said.

A rate the hardware cannot clock is refused where the `I2C` is constructed: the bit-rate
register has to stay at 10 or above in controller mode (about 444 kHz at 16 MHz) and tops out
at 255 with the prescaler at 1 (about 30.5 kHz). Below that, bit-bang the bus with
{doc}`softi2c <i2c>`, which has no such limit.

`frequency()` returns what the bus actually clocks, which is not always what was asked for:
the bit-rate register is an integer. 100 kHz and 400 kHz at 16 MHz are both exact.

### Status constants

| Constant | Value | Meaning |
|---|---|---|
| `I2C.START` | `0x08` | START condition transmitted |
| `I2C.RESTART` | `0x10` | Repeated START condition transmitted |
| `I2C.SLA_ACK` | `0x18` | SLA+W transmitted, ACK received |
| `I2C.SLA_NACK` | `0x20` | SLA+W transmitted, NACK received |
| `I2C.DATA_ACK` | `0x28` | Data byte transmitted, ACK received |
| `I2C.SLA_R_ACK` | `0x40` | SLA+R transmitted, ACK received |

### Methods

| Method | Description |
|---|---|
| `ping(addr: uint8) -> uint8` | Returns 1 if device ACKs, 0 if NACK |
| `start() -> uint8` | Send START condition, return status |
| `stop()` | Send STOP condition |
| `write(data: uint8) -> uint8` | Write byte, return TWI status |
| `write_to(addr: uint8, data: uint8) -> uint8` | START + SLA+W + byte + STOP |
| `writebyte(addr: uint8, data: uint8) -> uint8` | Same transaction as `write_to`; returns 1 on success, the failing TWI status (`0x20`/`0x30`) on a NACK, `0xFF` on a bus timeout |
| `write_bytes(addr: uint8, buf, n: uint8)` | Multi-byte write: START + SLA+W + N bytes + STOP |
| `read_from(addr: uint8) -> uint8` | START + SLA+R + read byte + NACK + STOP |
| `read_ack() -> uint8` | Read byte + send ACK (more data follows) |
| `read_nack() -> uint8` | Read byte + send NACK (last byte in transaction) |
| `__enter__() -> uint8` | Alias for `start()` — called by `with i2c:` |
| `__exit__()` | Alias for `stop()` — called by `with i2c:` |

Every status-returning method is `@inline`: a caller that ignores the return value pays
nothing — the status folds away — while a caller that reads it can branch on the NACK
without a second transaction. The compatibility layers (`busio.I2C`, `machine.I2C`) read
them to raise `OSError` the way upstream CircuitPython and MicroPython do.

---

## Examples

### Bus scanner

```python
from pymcu.hal.i2c import I2C
from pymcu.hal.uart import UART
from pymcu.types import uint8

def main():
    i2c = I2C()
    uart = UART(9600)
    uart.println("Scanning I2C bus...")

    addr: uint8 = 1
    while addr < 128:
        if i2c.ping(addr):
            uart.write_str("Found: 0x")
            uart.print_byte(addr)
        addr += 1
```

### Read a register (context manager)

```python
from pymcu.hal.i2c import I2C
from pymcu.types import uint8

def read_reg(i2c: I2C, dev_addr: uint8, reg: uint8) -> uint8:
    # Write register address
    with i2c:
        i2c.write((dev_addr << 1) | 0)   # SLA+W
        i2c.write(reg)

    # Read one byte
    result: uint8 = 0
    with i2c:
        i2c.write((dev_addr << 1) | 1)   # SLA+R
        result = i2c.read_nack()
    return result
```

### SoftI2C (bit-bang)

Use `SoftI2C` for arbitrary GPIO pins:

```python
from pymcu.hal.softi2c import SoftI2C
from pymcu.hal.avr.gpio import Pin
from pymcu.types import uint8

i2c = SoftI2C(sda=Pin("PD2", Pin.OUT), scl=Pin("PD3", Pin.OUT))
found: uint8 = i2c.ping(0x68)      # check MPU-6050
```
