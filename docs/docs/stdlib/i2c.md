# I2C — `whipsnake.hal.i2c`

```python
from whipsnake.hal.i2c import I2C
```

I2C (TWI) support is currently available for AVR (ATmega328P) only.

## `I2C()`

Initializes the TWI peripheral at 100 kHz. SDA = `PC4` (A4), SCL = `PC5` (A5).

### Status constants

| Constant | Value | Meaning |
|---|---|---|
| `I2C.START` | `0x08` | START condition transmitted |
| `I2C.SLA_ACK` | `0x18` | SLA+W transmitted, ACK received |
| `I2C.SLA_NACK` | `0x20` | SLA+W transmitted, NACK received |
| `I2C.DATA_ACK` | `0x28` | Data transmitted, ACK received |
| `I2C.SLA_R_ACK` | `0x40` | SLA+R transmitted, ACK received |

### Methods

| Method | Description |
|---|---|
| `ping(addr: uint8) -> uint8` | Returns 1 if device ACKs, 0 if NACK |
| `start() -> uint8` | Send START, return status |
| `stop()` | Send STOP condition |
| `end()` | Alias for `stop()` |
| `write(data: uint8) -> uint8` | Write byte, return status |
| `read_ack() -> uint8` | Read byte + send ACK |
| `read_nack() -> uint8` | Read byte + send NACK (last byte) |
| `__enter__() -> uint8` | Alias for `start()` — called by `with i2c:` |
| `__exit__()` | Alias for `stop()` — called by `with i2c:` |

## Context manager (`with` statement)

Use `with i2c:` to automatically send START/STOP around a transaction:

```python
with i2c:
    i2c.write(0xD0)    # start() called before, stop() called after
    i2c.write(0x3B)
```

## Example — I2C bus scanner

```python
from whipsnake.hal.i2c import I2C
from whipsnake.hal.uart import UART
from whipsnake.types import uint8

def main():
    i2c = I2C()
    uart = UART(9600)
    uart.println("Scanning...")

    addr: uint8 = 1
    while addr < 128:
        if i2c.ping(addr):
            uart.write_str("Found: 0x")
            uart.print_byte(addr)
        addr += 1
```

## Example — read register via `with`

```python
from whipsnake.hal.i2c import I2C
from whipsnake.types import uint8

def read_reg(i2c: I2C, dev_addr: uint8, reg: uint8) -> uint8:
    with i2c:
        i2c.write((dev_addr << 1) | 0)   # SLA+W
        i2c.write(reg)

    result: uint8 = 0
    with i2c:
        i2c.write((dev_addr << 1) | 1)   # SLA+R
        result = i2c.read_nack()
    return result
```
