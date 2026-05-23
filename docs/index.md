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

PyMCU is different: the **compiler** runs on your PC and produces tight AVR/PIC/RISC-V
assembly. The MCU receives only the resulting machine code — no interpreter, no garbage
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

| Architecture | Chips | Status |
|---|---|---|
| AVR | ATmega328P (Arduino Uno) | Complete |
| PIC14/14E | PIC16F84A, PIC16F877A, PIC16F18877 | Complete |
| PIC18 | PIC18F45K50 | Complete |
| PIC12 | PIC10F200 | Complete |
| RISC-V | CH32V003 | Partial |
| RP2040 PIO | PIO state machine | Partial |

---

```{toctree}
:maxdepth: 2
:hidden:
:caption: Getting Started

getting-started/index
```

```{toctree}
:maxdepth: 2
:hidden:
:caption: PyMCU Language

language/index
```

```{toctree}
:maxdepth: 2
:hidden:
:caption: PyMCU Libraries

library/index
```

```{toctree}
:maxdepth: 2
:hidden:
:caption: Compatibility Layers

compat/index
```

```{toctree}
:maxdepth: 2
:hidden:
:caption: Examples

examples/index
```

```{toctree}
:maxdepth: 2
:hidden:
:caption: Reference

reference/index
```
