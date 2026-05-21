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

---

## class `I2C`

### `I2C(freq: uint32 = 100000)`

Initializes the TWI peripheral. Default frequency is 100 kHz.

### Status constants

| Constant | Value | Meaning |
|---|---|---|
| `I2C.START` | `0x08` | START condition transmitted |
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
| `write_bytes(addr: uint8, buf, n: uint8)` | Multi-byte write: START + SLA+W + N bytes + STOP |
| `read_from(addr: uint8) -> uint8` | START + SLA+R + read byte + NACK + STOP |
| `read_ack() -> uint8` | Read byte + send ACK (more data follows) |
| `read_nack() -> uint8` | Read byte + send NACK (last byte in transaction) |
| `__enter__() -> uint8` | Alias for `start()` — called by `with i2c:` |
| `__exit__()` | Alias for `stop()` — called by `with i2c:` |

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
from pymcu.hal.i2c import SoftI2C
from pymcu.types import uint8

i2c = SoftI2C(sda="PD2", scl="PD3")
found: uint8 = i2c.ping(0x68)      # check MPU-6050
```
