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

`board`, `digitalio`, `analogio`, `pwmio`, `busio`, `time` modules. Compatible with
CircuitPython scripts targeting Arduino Uno pin names.

**Status:** GPIO, ADC, PWM complete; SPI/I2C planned
:::
::::

---

## How it works

The compat packages are **compile-time shims** — not runtime wrappers. Each API call is
translated directly to a PyMCU HAL call during compilation:

```
machine.Pin(13, Pin.OUT)  →  pymcu.hal.gpio.Pin("PB5", Pin.OUT)
utime.sleep_ms(500)       →  pymcu.time.delay_ms(500)
machine.ADC(0)            →  pymcu.hal.adc.AnalogPin("A0")
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
Features that fundamentally require a runtime interpreter — dynamic allocation, `float`
sleep arguments, exception handling, `__repr__`, REPL interaction — are not available.
See each page for the full differences table.
:::

```{toctree}
:maxdepth: 1
:hidden:

micropython
circuitpython
```
