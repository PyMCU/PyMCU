# UART — `pymcu.hal.uart`

```python
from pymcu.hal.uart import UART
```

## `UART(baud=9600)`

Initializes the hardware UART peripheral. On AVR, uses USART0.

| Baud rate | UBRR0 value (16 MHz) |
|---|---|
| 9600 | 103 |
| 19200 | 51 |
| 38400 | 25 |
| 57600 | 16 |
| 115200 | 8 |

### Methods

| Method | Description |
|---|---|
| `write(data: uint8)` | Send a single byte (blocks until UDRE0 is set) |
| `read() -> uint8` | Receive a byte (blocks until RXC0 is set) |
| `write_str(s: const[str])` | Send a flash string via LPM loop |
| `println(s: const[str])` | `write_str(s)` + newline (`\n`, 0x0A) |
| `print_byte(value: uint8)` | Print `value` as decimal digits + newline |

## Examples

```python
from pymcu.hal.uart import UART
from pymcu.types import uint8

uart = UART(9600)

# Send a byte
uart.write(65)         # 'A'

# Receive a byte (blocking)
b: uint8 = uart.read()

# Send a flash string
uart.write_str("Hello, world!")
uart.println("ready")

# Print a number
val: uint8 = 42
uart.print_byte(val)   # sends "42\n"
```

## UART echo loop

```python
from pymcu.hal.uart import UART
from pymcu.types import uint8

def main():
    uart = UART(9600)
    while True:
        b: uint8 = uart.read()
        uart.write(b)
```
