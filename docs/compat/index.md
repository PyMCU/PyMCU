# Compatibility Layers

PyMCU provides optional compatibility packages that let you write firmware using the
**MicroPython** or **CircuitPython** APIs you already know — and compile it to native
AVR machine code, with zero interpreter overhead.

> Write once in MicroPython or CircuitPython syntax. PyMCU compiles it to tight
> ATmega328P assembly. No interpreter on the chip, no runtime, no heap.

::::{grid} 1 2 2 2
:gutter: 3

:::{grid-item-card} MicroPython
:link: micropython
:link-type: doc

`machine`, `utime`, `micropython` modules. Compatible with MicroPython scripts targeting
GPIO, UART, SPI, I2C, ADC, Timer, WDT, and power management.

**Status:** Available — production ready
:::

:::{grid-item-card} CircuitPython
:link: circuitpython
:link-type: doc

`board`, `digitalio`, `analogio`, `pwmio`, `busio`, `neopixel`, `time`, `supervisor`,
`alarm`, and `microcontroller` modules. Compatible with CircuitPython scripts targeting
Arduino Uno, Arduino Mega, and ATtiny boards.

**Status:** Available — all core modules complete
:::
::::

---

## How it works

The compat packages are **compile-time shims** — not runtime wrappers. Each API call is
translated directly to a PyMCU HAL call during compilation:

```
machine.Pin(13, Pin.OUT)  →  pymcu.hal.gpio.Pin("PB5", Pin.OUT)
utime.sleep_ms(500)       →  pymcu.time.delay_ms(500)
machine.ADC(Pin(14))      →  pymcu.hal.adc.AnalogPin("PC0")
```

The compiled firmware is identical to one written with the native PyMCU HAL — the compat
layer has **zero runtime cost**.

---

## Activate a compatibility layer

In `pyproject.toml`:

```toml
[tool.pymcu]
chip    = "atmega328p"
stdlib  = ["micropython"]   # or "circuitpython"
```

Then import normally:

```python
from machine import Pin, UART
import utime
```

---

:::{important} These are shims, not full implementations
Features that fundamentally require a runtime interpreter — unbounded allocation, `float`
sleep arguments, a user `__repr__`, REPL interaction — are not available. (`try / except /
finally` and `raise` **are** supported on AVR via the zero-cost T-flag model, and `print()`
does emit the built-in CPython repr for a `bytearray` or an array slice of compile-time
length.) See each page for the full differences table.

Where the hardware cannot honour an API, the layer raises a `CompileError` naming the
alternative rather than accepting the argument and quietly doing something else —
`machine.I2C(scl=...)` on fixed TWI pins, `machine.unique_id()` on a chip with no unique ID,
`UART.readline()` with no buffer.
:::

```{toctree}
:maxdepth: 1
:hidden:

micropython
circuitpython
```
