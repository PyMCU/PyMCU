# Example: UART Echo

Sends a startup banner, then echoes every received byte back on UART0.

**Flash:** 170 bytes &ensp; **SRAM:** 0 bytes

---

## Source

```python
from pymcu.types import uint8
from pymcu.hal.uart import UART

def main():
    uart = UART(9600)
    uart.println("ECHO READY")

    while True:
        b: uint8 = uart.read()
        uart.write(b)
```

---

## How it compiles

- `UART(9600)` inlines the initialization sequence. UBRR0 is pre-computed at compile time
  to 103 for a 16 MHz clock — no division happens at runtime.
- `uart.println("ECHO READY")` places the string literal in flash (PROGMEM on AVR) and
  emits an LPM loop that sends each byte to UDR0.
- `uart.read()` busy-waits on `UCSR0A[7]` (RXC0) then reads `UDR0`.
- `uart.write(b)` busy-waits on `UCSR0A[5]` (UDRE0) then writes `UDR0`.

Zero SRAM is used — no buffer, no ring buffer — pure register-level I/O.

---

## Build and flash

```bash
cd examples/avr/uart-echo
pymcu build
pymcu flash --port /dev/cu.usbmodem*
```

Open a serial monitor at 9600 baud (e.g. `screen /dev/cu.usbmodem* 9600`). Every character
you type is echoed back.

---

## Variant: non-blocking receive

```python
from pymcu.types import uint8
from pymcu.hal.uart import UART
from pymcu.hal.gpio import Pin

def main():
    uart = UART(9600)
    led = Pin("PB5", Pin.OUT)

    while True:
        if c := uart.read_nb():   # walrus: read returns 0 if no byte ready
            uart.write(c)
            led.toggle()
```
