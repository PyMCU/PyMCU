# Welcome to PyMCU

:::{admonition} Alpha Software
:class: warning

PyMCU is currently in **early alpha**. It is under active development and requires a local source build.
The standard library is being aligned with the MicroPython/CircuitPython APIs for compatibility,
which means major breaking changes are expected.
:::

**PyMCU** compiles a **statically-typed, allocation-free subset of Python** directly to
bare-metal microcontroller machine code — no runtime, no heap, no interpreter.

:::{important} This is not standard Python
PyMCU accepts Python *syntax* but enforces a strict compile-time type system. It is a
**compiler**, not an interpreter. Code runs at native MCU speed with zero runtime overhead.
Heap allocation, exceptions, closures, and dynamic features do not exist.
:::

```python
from pymcu.hal.gpio import Pin
from pymcu.time import delay_ms

def main():
    led = Pin("PB5", Pin.OUT)   # type resolved at compile time
    while True:
        led.toggle()
        delay_ms(500)
```

```bash
pymcu build   # → dist/firmware.hex  (124 bytes flash, 0 bytes SRAM)
pymcu flash   # → avrdude upload to Arduino Uno
```

---

## Compiled, not interpreted

Most Python-on-microcontrollers systems (MicroPython, CircuitPython) embed a full Python
interpreter in flash, consuming 200–300 KB and running bytecode at runtime.

PyMCU is different: the **compiler** runs on your PC and produces tight AVR assembly for the
ATmega328P. The MCU receives only the resulting machine code — no interpreter, no garbage
collector, no runtime.

::::{grid} 1 2 2 3
:gutter: 3

:::{grid-item-card} Zero runtime overhead
The compiler resolves all types, inlines HAL calls, and eliminates dead branches. No
interpreter loop, no bytecode dispatch.
:::

:::{grid-item-card} Minimal flash footprint
A blink program compiles to ~124 bytes. MicroPython needs ~256 KB before your code even starts.
:::

:::{grid-item-card} Python syntax you already know
Write `if`, `for`, `class`, `match/case`, type annotations — the compiler handles the rest.
:::
::::

---

## How it compares

| | Arduino (C++) | MicroPython | CircuitPython | **PyMCU** |
|---|---|---|---|---|
| Language | C++ | Python | Python | **Python subset** |
| Execution | Native | Interpreted | Interpreted | **Native (compiled)** |
| Runtime | None | ~256 KB | ~256 KB | **None** |
| Heap | None | Yes | Yes | **None** |
| Flash footprint | Small | Large | Large | **Minimal** |
| Static types | No | No | No | **Yes (required)** |

---

## Supported hardware

PyMCU targets the **ATmega328P** (Arduino Uno, Arduino Nano). It compiles directly to AVR
machine code — no external assembler required.

| Board | Chip | Flash | SRAM |
|---|---|---|---|
| Arduino Uno | ATmega328P @ 16 MHz | 32 KB | 2 KB |
| Arduino Nano | ATmega328P @ 16 MHz | 32 KB | 2 KB |

---

## MicroPython & CircuitPython compatible

Already know MicroPython or CircuitPython? PyMCU ships compatibility shims that let you write
firmware using the APIs you already know — and compile it to native machine code.

::::{grid} 1 2 2 2
:gutter: 3

:::{grid-item-card} MicroPython compat
:link: compat/micropython
:link-type: doc

Use `machine.Pin`, `machine.UART`, `machine.Timer`, `machine.ADC`, `machine.SPI`,
`machine.I2C` and `utime` — compiled to zero-overhead AVR code.

```python
from machine import Pin
import utime

led = Pin(13, Pin.OUT)
while True:
    led.toggle()
    utime.sleep_ms(500)
```
:::

:::{grid-item-card} CircuitPython compat
:link: compat/circuitpython
:link-type: doc

Use `board`, `digitalio.DigitalInOut`, `analogio.AnalogIn`, `pwmio.PWMOut` and `time`
— the same API you'd use on a Circuit Playground or Feather, compiled to AVR.

```python
import board
import digitalio
import time

led = digitalio.DigitalInOut(board.LED)
led.direction = digitalio.Direction.OUTPUT
while True:
    led.value = not led.value
    time.sleep(0.5)
```
:::
::::

---

```{toctree}
:maxdepth: 1
:hidden:
:caption: Getting Started

getting-started/index
```

```{toctree}
:maxdepth: 1
:hidden:
:caption: Compatibility Layers

compat/index
```

```{toctree}
:maxdepth: 1
:hidden:
:caption: PyMCU Language

language/index
```

```{toctree}
:maxdepth: 1
:hidden:
:caption: PyMCU Libraries

library/index
```

```{toctree}
:maxdepth: 1
:hidden:
:caption: Examples

examples/index
```

```{toctree}
:maxdepth: 1
:hidden:
:caption: Reference

reference/index
```
