# UART — `pymcu.hal.uart`

```python
from pymcu.hal.uart import UART
```

Serial communication over USART0. `UART` is an `@inline` class — it compiles to USART register
writes with zero SRAM allocation.

---

## class `UART`

### `UART(baud=9600)`

Initializes the hardware UART peripheral. On AVR, configures USART0.

| Baud rate | UBRR0 (16 MHz) |
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
| `read_nb() -> uint8` | Non-blocking read; returns byte if available, else 0 |
| `read_blocking() -> uint8` | Poll RXC until byte arrives and return it |
| `read_byte_isr() -> uint8` | Direct UDR0 read for use inside `@interrupt` handlers |
| `available() -> uint8` | Returns 1 if a byte is waiting in the receive buffer |
| `write_str(s: const[str])` | Send a flash string via LPM loop |
| `println(s: const[str])` | `write_str(s)` + newline (0x0A) |
| `print_byte(value: uint8)` | Print `value` as decimal digits + newline |
| `enable_rx_interrupt()` | Enable RXC interrupt (RXCIE0 in UCSR0B) |
| `rx_isr()` | ISR body: reads UDR0 into ring buffer |

---

## Examples

### Hello, world

```python
from pymcu.hal.uart import UART

uart = UART(9600)
uart.println("hello")
```

### Send and receive bytes

```python
from pymcu.hal.uart import UART
from pymcu.types import uint8

uart = UART(9600)
uart.write(65)          # sends 'A'

b: uint8 = uart.read()  # blocking receive
uart.write(b)           # echo back
```

### Echo loop

```python
from pymcu.hal.uart import UART
from pymcu.types import uint8

def main():
    uart = UART(9600)
    while True:
        b: uint8 = uart.read()
        uart.write(b)
```

### Non-blocking receive with walrus

```python
from pymcu.hal.uart import UART
from pymcu.types import uint8

def main():
    uart = UART(9600)
    while True:
        if c := uart.read_nb():     # walrus: assign and test in one
            uart.write(c)
```

### Interrupt-driven ring buffer

```python
from pymcu.hal.uart import UART
from pymcu.types import uint8

uart = UART(9600)

@interrupt(0x0018)    # USART0 RX Complete (ATmega328P)
def on_rx():
    uart.rx_isr()     # stores byte in ring buffer

def main():
    uart.enable_rx_interrupt()
    while True:
        if uart.available():
            b: uint8 = uart.read_nb()
            uart.write(b)
```
