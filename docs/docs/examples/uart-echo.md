# Example: uart-echo

Sends a startup banner then echoes every received byte back on UART0.

**Flash:** 170 bytes  **SRAM:** 0 bytes

## Source

```python
from whisnake.types import uint8
from whisnake.hal.uart import UART

def main():
    uart = UART(9600)
    uart.write(69)    # E
    uart.write(67)    # C
    uart.write(72)    # H
    uart.write(79)    # O
    uart.write(10)    # \n

    while True:
        b: uint8 = uart.read()
        uart.write(b)
```

## Key points

- `UART(9600)` inlines the initialization sequence — UBRR0 is pre-computed at compile time to
  103 for a 16 MHz clock.
- `uart.read()` blocks until `UCSR0A[7]` (RXC0) is set.
- `uart.write(b)` blocks until `UCSR0A[5]` (UDRE0) is set, then writes to `UDR0`.

## Build

```bash
cd examples/avr/uart-echo
whip build
whip flash --port /dev/cu.usbmodem*
```

Then open a serial monitor at 9600 baud.
