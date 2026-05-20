# PyMCU

!!! warning "Alpha software"
    PyMCU is in early alpha. Core AVR compilation is solid and test-covered, but
    error messages, tooling, and some language edges are still rough. Feedback
    welcome — breakage expected.

**PyMCU** compiles a **statically-typed, allocation-free subset of Python** directly to
bare-metal MCU machine code — no runtime, no heap, no interpreter.

!!! important "This is not standard Python"
    PyMCU accepts Python *syntax* but enforces a strict compile-time type system.
    The following standard Python features **do not exist** in PyMCU:
    `list.append`, `dict`, `set`, `try/except`, `f"..."` with runtime values,
    closures, `*args`, `async/await`, and any heap allocation.
    See [Language Limitations](limitations.md) for the full list.

What working PyMCU code looks like:

```python
from pymcu.hal.gpio import Pin
from pymcu.time import delay_ms

def main():
    led = Pin("PB5", Pin.OUT)  # type resolved at compile time
    while True:
        led.toggle()
        delay_ms(500)
```

```bash
pymcu build   # → dist/firmware.hex  (124 bytes flash, 0 bytes SRAM)
pymcu flash   # → avrdude upload to Arduino Uno
```

---

## Why PyMCU?

| | Arduino (C++) | MicroPython | CircuitPython | **PyMCU** |
|---|---|---|---|---|
| Language | C++ | Python | Python | **Python subset** |
| Runtime | None | Interpreter | Interpreter | **None** |
| Heap | None | Yes | Yes | **None** |
| Flash footprint | Small | ~256 KB | ~256 KB | **Minimal** |
| Python syntax | Partial | Full | Full | **Typed subset** |
| Static types | No | No | No | **Yes (required)** |

PyMCU occupies the gap between "write C++" and "run MicroPython": you write Python, but the
compiler produces tight AVR assembly with zero runtime overhead.

---

## Quick Start

### Installation

```bash
pipx install pymcu
```

### Create a project

```bash
pymcu new my_project
cd my_project
```

### Build and flash

```bash
pymcu build
pymcu flash --port /dev/cu.usbmodem*
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
- [CircuitPython migration](migration/from-circuitpython.md) — port CP code to PyMCU
- [MicroPython migration](migration/from-micropython.md) — port uPython code to PyMCU
