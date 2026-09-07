# Welcome to PyMCU

:::{admonition} Alpha Software
:class: warning

PyMCU is currently in **early alpha**. The standard library is being aligned with the
MicroPython/CircuitPython APIs for compatibility — breaking changes are expected between
releases.
:::

**PyMCU** compiles a **statically-typed, allocation-free subset of Python** directly to
bare-metal microcontroller machine code — no runtime, no heap, no interpreter.

:::{important} This is not standard Python
PyMCU accepts Python *syntax* but enforces a strict compile-time type system. It is a
**compiler**, not an interpreter. Code runs at native MCU speed with zero runtime overhead.
Unbounded containers, runtime reflection and the interpreter-only features do not exist —
though `dict` and `set` literals do bind compile-time lookup tables, and `list[T]` /
`FixedDict` give you mutation within a footprint fixed at compile time. (`try / except /
finally` and `raise` *are* supported on AVR via a zero-cost T-flag model.)
:::

```bash
pip install --pre pymcu-compiler
```

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
pymcu build   # → dist/firmware.hex  (36 bytes flash, 0 bytes SRAM)
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
A blink program compiles to ~36 bytes of user code (142 bytes total firmware). MicroPython needs ~256 KB before your code even starts.
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
| Source debugger | — | serial only | serial only | **VS Code DAP** |
| CPU profiler | — | — | — | **Speedscope flamegraph** |

---

## Supported hardware

The compiler frontend and the **AVR** backend are **beta** as of 0.1.0b1. **ARM, PIC and
RISC-V remain alpha**: they build and run, but parts of the language surface are missing
on them, they do not carry AVR's continuous silicon validation, and their APIs may change
between releases.

PyMCU's primary target is the **AVR** family. The reference board is the
**Arduino Uno / ATmega328P** — all AVR integration tests run against it, and it is the
board the release is validated on with a logic analyzer.

| Board | Chip | Flash | SRAM |
|---|---|---|---|
| Arduino Uno | ATmega328P @ 16 MHz | 32 KB | 2 KB |
| Arduino Nano | ATmega328P @ 16 MHz | 32 KB | 2 KB |
| Arduino Mega 2560 | ATmega2560 @ 16 MHz | 256 KB | 8 KB |
| ATtiny85 / 84 / 45 / 44 / 25 / 24 | ATtiny family | 2–8 KB | 256–512 B |
| ATtiny2313 / 4313 | ATtiny family | 2–4 KB | 128–256 B |
| Digispark | ATtiny85 @ 16 MHz | 8 KB | 512 B |

### Raspberry Pi Pico (RP2040 / RP2350), alpha

The **RP2040** and **RP2350** are supported through the
{doc}`ARM backend <getting-started/installation>` (`pymcu-compiler[arm]`), which lowers
PyMCU's IR to LLVM IR (`thumbv6m-none-eabi` / `thumbv8m.main-none-eabi`).

| | RP2040 / RP2350 (alpha) |
|---|---|
| Cores | Core 0 only |
| Peripherals | GPIO, UART, SPI, I2C, PWM, ADC, DMA, PIO; CYW43 WiFi on the Pico 2 W |
| Language | Everything the AVR backend accepts **except** the heap-bounded `list[T]`; exceptions, `float`, f-strings, generators and `async`/`await` all compile |
| Output | `dist/firmware.bin` (flat flash, boot2 at offset 0) and `firmware.uf2` |

The same `Pin` / `UART` HAL — and the MicroPython (`machine`) and CircuitPython
(`board`, `digitalio`, `busio`) shims — compile to the Pico. Note that `board.LED` is not
defined for the RP chips: pass the GP number instead (`digitalio.DigitalInOut(25)`). See
{doc}`language/limitations` for the exact scope and {doc}`examples/rp2040` for runnable
programs.

### PIC16, alpha

The **PIC16F84A** and **PIC16F877A** are supported through the PIC backend
(`pymcu-compiler[pic]`), assembling through gputils / gpasm.

| | PIC16 (alpha) |
|---|---|
| Language | No `float`, no f-strings, no generators, no `async`, no `@interrupt`, and no general `try`/`except`, only the `ZeroDivisionError` guard. Use return codes |
| Fuses | Builds emit **no configuration word**, so the image will not boot until you program the fuses yourself. The build warns about this |
| Output | `dist/firmware.hex`; `pymcu flash` drives a PICkit 2 by default |

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

Use `board`, `digitalio`, `analogio`, `busio`, `pwmio`, `neopixel`, `time`,
`supervisor`, `alarm`, and `microcontroller` — the same API you'd use on a
Circuit Playground or Feather, compiled to bare-metal AVR.

```python
import board
import time
from digitalio import DigitalInOut, Direction

led = DigitalInOut(board.LED)
led.direction = Direction.OUTPUT
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
