<!-- PROJECT LOGO -->
<br />
<p align="center">
  <a href="https://github.com/PyMCU/PyMCU">
    <img src="https://raw.githubusercontent.com/PyMCU/PyMCU/main/docs/_static/images/logo-icon.png" alt="PyMCU" width="200" height="200">
  </a>

  <h3 align="center">PyMCU</h3>

  <p align="center">
    Python to bare-metal firmware — no runtime, no interpreter, no VM.
    <br />
    <a href="https://github.com/PyMCU/PyMCU"><strong>Explore the project »</strong></a>
    <br />
    <br />
    <a href="https://github.com/PyMCU/PyMCU/issues">Report a bug</a>
    ·
    <a href="https://github.com/PyMCU/PyMCU/issues">Request a feature</a>
    ·
    <a href="https://github.com/sponsors/begeistert">Sponsor</a>
  </p>

  <p align="center">
    <a href="https://pypi.org/project/pymcu-compiler/">
      <img src="https://img.shields.io/pypi/v/pymcu-compiler?label=pymcu-compiler&color=blue" alt="PyPI version">
    </a>
    <a href="https://pypi.org/project/pymcu-compiler/">
      <img src="https://img.shields.io/pypi/pyversions/pymcu-compiler" alt="Python versions">
    </a>
    <a href="https://github.com/PyMCU/PyMCU/blob/main/LICENSE">
      <img src="https://img.shields.io/github/license/PyMCU/PyMCU" alt="License">
    </a>
    <a href="https://github.com/PyMCU/PyMCU/commits/main">
      <img src="https://img.shields.io/github/last-commit/PyMCU/PyMCU" alt="Last commit">
    </a>
    <a href="https://github.com/PyMCU/PyMCU/issues">
      <img src="https://img.shields.io/github/issues/PyMCU/PyMCU" alt="Open issues">
    </a>
    <a href="https://github.com/sponsors/begeistert">
      <img src="https://img.shields.io/badge/sponsor-%E2%9D%A4-ea4aaa?logo=github-sponsors" alt="Sponsor">
    </a>
  </p>
</p>

---

> [!IMPORTANT]
> **Alpha 3 is out — [v0.1.0a3 release notes](https://github.com/PyMCU/PyMCU/releases/tag/v0.1.0a3).**
> The language grows **generators**, **async/await**, **dict/set literals**, f-strings as
> values and type inference; the ARM targets (**RP2040 / RP2350**) reach feature parity
> with AVR — exceptions, floats, WiFi; and a brand-new **PIC backend** joins the family.
> Core compilation is stable and test-covered, but rough edges remain in error messages
> and tooling — if you hit a bug, [please open an issue](https://github.com/PyMCU/PyMCU/issues),
> it helps a lot.
>
> **Avoid `pymcu.hal.*`** during the alpha — the native HAL API may change between releases.
> Use the **MicroPython** or **CircuitPython** compat API instead; those are stable and
> community-specified.

PyMCU compiles a **statically-typed subset of Python** into bare-metal firmware for
**AVR, ARM (RP2040 / RP2350) and PIC** — no runtime, no interpreter, no virtual machine.
The same binary you would write in C.

---

## The pitch in one table

LED blink for ATmega328P @ 16 MHz — all variants do the same thing:
configure PB5 as output, then loop `LED on → wait 500 ms → LED off → wait 500 ms` forever.

| Source | **Total flash** | SRAM |
|---|---|---|
| **C** (`avr-gcc -Os`) | 176 B | 0 B |
| **PyMCU** (native HAL) | **162 B** | 0 B |
| **PyMCU** (MicroPython API) | **162 B** | 0 B |
| **PyMCU** (CircuitPython API) | **164 B** | 0 B |
| **Arduino** (IDE defaults) | ~1 024 B | 9 B |

PyMCU produces a **smaller binary than C** here. Why?

`Pin("PB5", Pin.OUT)` and `delay_ms(500)` are resolved entirely at compile time — the
compiler sees through the Python objects and emits the same raw `SBI`/`CBI` port-toggle
instructions a C programmer would write by hand. The delay loop PyMCU generates happens to
use one fewer padding instruction than the loop `avr-gcc -Os` emits for this particular pattern.
The interrupt vector table and startup stub are identical fixed overhead in both toolchains.

**Native HAL and MicroPython API produce byte-for-byte identical firmware** — both compile down to the same `SBI`/`CBI` toggle and the same delay loop. The API is a zero-cost abstraction. CircuitPython is 2 bytes larger because the `Direction.OUTPUT` setter clears the PORT register before setting DDR, as the CircuitPython spec requires.

> These numbers are for a minimal blink. Real programs that use SRAM (global variables, buffers) will emit a small zeroing loop at startup, just like C does.

> **For complex drivers** (custom protocols, timing-critical bit-bang): expect 2-3x flash vs
> hand-written C. PyMCU is not competing with C — the goal is to make microcontroller
> development approachable in Python you already know, without the overhead of Arduino.
> The output is still 100-1000x smaller than any embedded Python interpreter.

---

## Write code you already know

Pick the API that fits your background. Both compile to the same bare-metal firmware.

### CircuitPython


```python
# The exact same code that runs on a Pico under CircuitPython
import board
import digitalio
from time import sleep_ms

led = digitalio.DigitalInOut(board.LED)
led.direction = digitalio.Direction.OUTPUT

while True:
    led.value = True
    sleep_ms(500)
    led.value = False
    sleep_ms(500)
```

### MicroPython

```python
# The exact same code that runs on a Pico under MicroPython
from machine import Pin
from utime import sleep_ms

led = Pin(13, Pin.OUT)

while True:
    led.value(1)
    sleep_ms(500)
    led.value(0)
    sleep_ms(500)
```

```bash
pymcu build   # → dist/firmware.hex  (56 bytes flash, 0 bytes SRAM)
pymcu flash   # → avrdude upload to Arduino Uno
```

---

## First binary in under 5 minutes

### 1. Install

```bash
pipx install --pip-args=--pre "pymcu-compiler[avr]"    # AVR (ATmega / ATtiny)
pipx install --pip-args=--pre "pymcu-compiler[arm]"    # RP2040 / RP2350 (Pico / Pico 2)
pipx install --pip-args=--pre "pymcu-compiler[pic]"    # PIC16
pipx install --pip-args=--pre "pymcu-compiler[all]"    # everything
```

Requires Python 3.11+ and `pipx`. Each extra bundles its full toolchain
(compiler backend + assembler/linker binaries) — no system packages needed.

> **Package name:** PyMCU is published as `pymcu-compiler` on PyPI while a
> [PEP 541 request](https://github.com/pypa/pypi-support) to reclaim the `pymcu`
> name is under review. Once approved, a `pymcu` metapackage will alias
> `pymcu-compiler` — installs and project configs will stay compatible.

### 2. Create a project

```bash
pymcu new blink
cd blink
```

### 3. Choose your API and write the program

**CircuitPython style** — add `pymcu-circuitpython` to dependencies:

```toml
# pyproject.toml
[project]
dependencies = ["pymcu-compiler[avr]", "pymcu-circuitpython"]

[tool.pymcu]
board     = "arduino_uno"
frequency = 16000000
```

```python
# src/main.py
import board
import digitalio
from time import sleep_ms

led = digitalio.DigitalInOut(board.LED)
led.direction = digitalio.Direction.OUTPUT

while True:
    led.value = True
    sleep_ms(500)
    led.value = False
    sleep_ms(500)
```

**MicroPython style** — add `pymcu-micropython` to dependencies:

```toml
# pyproject.toml
[project]
dependencies = ["pymcu-compiler[avr]", "pymcu-micropython"]

[tool.pymcu]
board     = "arduino_uno"
frequency = 16000000
```

```python
# src/main.py
from machine import Pin
from utime import sleep_ms

led = Pin(13, Pin.OUT)

while True:
    led.value(1)
    sleep_ms(500)
    led.value(0)
    sleep_ms(500)
```

### 4. Build and flash

```bash
pymcu build
# Compiling src/main.py...
# → dist/firmware.hex

pymcu flash --port /dev/cu.usbmodem*
# avrdude: flash verified
```

---

## Choosing an API

| Package | API surface | Install |
|---|---|---|
| `pymcu-circuitpython` | `digitalio`, `analogio`, `busio`, `pwmio`, `time`, `board`, `neopixel` | `pip install pymcu-circuitpython` |
| `pymcu-micropython` | `machine` (Pin/UART/ADC/PWM/SPI/I2C/Timer/WDT), `utime` | `pip install pymcu-micropython` |
| `pymcu.hal.*` | Direct register-level HAL — lowest overhead | `pymcu-stdlib` (installed automatically with `pymcu-compiler`) |

**Start with MicroPython or CircuitPython** — they are stable, community-specified,
and backed by real hardware compatibility guarantees. The `pymcu.hal.*` native HAL is
functional but its API **may change between alpha releases** — avoid it unless you need
direct register access not yet covered by the compat layers.

---

## Supported targets

| Architecture | Chips |
|---|---|
| **AVR** (ATmega) | ATmega48/88/168/328P, ATmega2560, ATmega32U4 |
| **AVR** (ATtiny) | ATtiny25/45/85, ATtiny24/44/84, ATtiny13/13A, ATtiny2313/4313 |
| **ARM** (Cortex-M0+ / M33) | RP2040 (Pico / Pico W), RP2350 (Pico 2 / Pico 2 W) — incl. PIO and CYW43 WiFi |
| **PIC** (mid-range) | PIC16F84A, PIC16F877A — new in alpha 3 |

---

## HAL coverage (ATmega328P / Arduino Uno)

| Module | Features |
|---|---|
| `pymcu.hal.gpio` | `Pin` — high / low / toggle / irq / pulse_in |
| `pymcu.hal.uart` | `UART` — write / read / println / RX interrupt |
| `pymcu.hal.adc` | `AnalogPin` — poll + interrupt; internal temperature |
| `pymcu.hal.timer` | `Timer(n, prescaler)` — CTC mode; `millis()` / `micros()` |
| `pymcu.hal.pwm` | `PWM` — multi-channel; `set_duty` / `set_freq` |
| `pymcu.hal.spi` | `SPI` + `SoftSPI` |
| `pymcu.hal.i2c` | `I2C` + `SoftI2C` |
| `pymcu.hal.eeprom` | `EEPROM` — `write(addr, val)` / `read(addr)` |
| `pymcu.hal.watchdog` | `Watchdog` — `enable` / `disable` / `feed` |
| `pymcu.hal.power` | `sleep_idle` / `power_down` / `standby` |

Drivers: DHT11, DS18B20, LM35, HD44780 LCD, SSD1306 OLED, MAX7219, BMP280, WS2812 NeoPixel.

---

## What Python features are supported

PyMCU accepts Python syntax but enforces a strict compile-time type system.

**Supported:**
- Integer types: `uint8`, `int8`, `uint16`, `int16`, `uint32`, `int32`, `float` — with
  type inference for unannotated `def` parameters and returns
- Fixed arrays `buf: uint8[16]`, heap-bounded lists `x: list[uint8] = list()`,
  equal-length slice assignment
- `for`, `while`, `if`, `match / case`, `with`, `class`, `@inline`, `lambda`
- **Generators** (`yield`), **`async` / `await`** with `asyncio.run` / `gather`
- **`dict` / `set` literals** as closed compile-time lookup tables, plus
  `pymcu.collections.FixedDict` for mutable fixed-capacity maps — still no heap
- **f-strings** with runtime interpolations and format specs, as stream writes or values
- `try / except / raise / finally` with cross-function propagation (AVR **and** ARM)
- `@interrupt` ISR handlers, `asm("...")` inline assembly (with operands on ARM)
- CircuitPython and MicroPython compat packages, plus `pymcu lint` to vet a port

**Not supported:**
- Open-ended `dict` / `set` mutation beyond `FixedDict`'s fixed capacity (no heap hash tables)
- Closures capturing mutable variables — use explicit parameters
- `*args` / `**kwargs`, reflection (`getattr` / `setattr` / `eval`)

The compiler rejects unsupported features with a clear error at compile time.
See the [Language Limitations](docs/language/limitations.md) page for the full list.

---

## CLI reference

| Command | Description |
|---|---|
| `pymcu new <name>` | Scaffold a new project |
| `pymcu build` | Compile `src/` → `dist/firmware.hex` |
| `pymcu flash` | Upload via avrdude |
| `pymcu clean` | Remove build artifacts |

---

## Sustainability

Post-alpha development will be slower and community-driven. If PyMCU saves you time,
consider sponsoring the project — the goal is $200-300/month to cover the AI tooling
costs that made this first release possible and keep active development going.

[Sponsor on GitHub](https://github.com/sponsors/begeistert)

---

## License

All components are licensed under the [MIT License](LICENSE).
Your compiled firmware output is entirely yours — no runtime license, no attribution required.

---

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) and [LANGUAGE_ROADMAP.md](LANGUAGE_ROADMAP.md).

## Credits

Special thanks to Richard Wardlow, creator of the original
[pyMCU](https://github.com/rwardlow/pyMCU) project (2012).
See [CREDITS.md](CREDITS.md) for the full acknowledgement.
