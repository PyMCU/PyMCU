# Whipsnake

**Whipsnake** compiles a statically-typed, allocation-free subset of Python directly to bare-metal
MCU machine code — no runtime, no heap, no interpreter.

Write familiar Python. Flash to an ATmega328P (Arduino Uno) in seconds.

```python
from whipsnake.hal.gpio import Pin
from whipsnake.time import delay_ms

def main():
    led = Pin("PB5", Pin.OUT)
    while True:
        led.toggle()
        delay_ms(500)
```

```bash
whip build   # → dist/firmware.hex  (124 bytes flash, 0 bytes SRAM)
whip flash   # → avrdude upload to Arduino Uno
```

---

## Why Whipsnake?

| | Arduino (C++) | MicroPython | CircuitPython | **Whipsnake** |
|---|---|---|---|---|
| Language | C++ | Python | Python | **Python** |
| Runtime | None | Interpreter | Interpreter | **None** |
| Heap | None | Yes | Yes | **None** |
| Flash footprint | Small | ~256 KB | ~256 KB | **Minimal** |
| Familiar syntax | Partial | Full | Full | **Full** |
| Static types | No | No | No | **Yes (required)** |

Whipsnake occupies the gap between "write C++" and "run MicroPython": you write Python, but the
compiler produces tight AVR assembly with zero runtime overhead.

---

## Quick Start

### Installation

```bash
pipx install whipsnake
```

### Create a project

```bash
whip new my_project
cd my_project
```

### Build and flash

```bash
whip build
whip flash --port /dev/cu.usbmodem*
```

---

## Architecture Support

| Architecture | Chips | Status |
|---|---|---|
| AVR | ATmega328P (Arduino Uno) | Complete |
| PIC14/14E | PIC16F84A, PIC16F877A, PIC16F18877 | Complete |
| PIC18 | PIC18F45K50 | Complete |
| PIC12 | PIC10F200 | Complete |
| RISC-V | CH32V003 | Partial |
| RP2040 PIO | PIO state machine | Partial |

---

## Next Steps

- [Language Reference](language-reference.md) — complete syntax and type reference
- [Standard Library](stdlib/index.md) — GPIO, UART, ADC, Timer, SPI, I2C
- [Examples Gallery](examples/index.md) — 30+ annotated firmware examples
- [CircuitPython migration](migration/from-circuitpython.md) — port CP code to Whipsnake
- [MicroPython migration](migration/from-micropython.md) — port uPython code to Whipsnake
