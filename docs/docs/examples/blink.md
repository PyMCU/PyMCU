# Example: blink

Toggles the built-in LED on Arduino Uno (pin 13 / PB5) at 1 Hz.

**Flash:** 124 bytes  **SRAM:** 0 bytes

## Source

```python
from pymcu.hal.gpio import Pin
from pymcu.time import delay_ms

def main():
    led = Pin("PB5", Pin.OUT)
    while True:
        led.high()
        delay_ms(1000)
        led.low()
        delay_ms(1000)
```

## Key points

- `Pin("PB5", Pin.OUT)` is zero-cost: the `__init__` body is inlined, no SRAM is used for the
  pin object — the port/ddr/bit constants are resolved at compile time.
- `delay_ms(1000)` calls a non-inline helper (`_delay_1ms_avr`) 1000 times, avoiding label
  duplication in the assembler.

## Build

```bash
cd examples/avr/blink
pymcu build
pymcu flash --port /dev/cu.usbmodem*
```
