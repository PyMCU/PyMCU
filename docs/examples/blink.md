# Example: Blink

Toggles the built-in LED on Arduino Uno (pin 13 / PB5) at 1 Hz.

**Flash:** 56 bytes (user code) &ensp; **SRAM:** 0 bytes

---

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

---

## How it compiles

`Pin("PB5", Pin.OUT)` is zero-cost. The `Pin` class is `@inline` — its constructor
expands to two register writes (set DDR bit, set initial PORT value). No SRAM is allocated
for the pin object; the port/ddr/bit constants are resolved at compile time.

`delay_ms(1000)` calls a non-inline helper `_delay_1ms_avr` in a loop, avoiding label
duplication in the assembler.

The entire program compiles to approximately 30 AVR instructions.

---

## Build and flash

```bash
cd examples/avr/blink
pymcu build
pymcu flash --port /dev/cu.usbmodem*    # macOS
pymcu flash --port /dev/ttyACM0         # Linux
pymcu flash --port COM3                 # Windows
```

---

## Variant: using board pin names

```python
from pymcu.hal.gpio import Pin
from pymcu.boards.arduino_uno import LED_BUILTIN
from pymcu.time import delay_ms

def main():
    led = Pin(LED_BUILTIN, Pin.OUT)
    while True:
        led.toggle()
        delay_ms(500)
```
