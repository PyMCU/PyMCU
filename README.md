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
> **Alpha 10 is out — [v0.1.0a10 release notes](https://github.com/PyMCU/PyMCU/releases/tag/v0.1.0a10).**
> The hardware-validation release. It came out of a sustained bug hunt on a real Arduino
> Uno with a logic analyzer, plus a sweep of the official MicroPython quickref and
> CircuitPython Essentials examples — 63 projects, 53 of which compile; the rest fail on
> purpose with a clear diagnostic.
>
> What that hunt found was a class of **silent miscompiles**: `uint32(float_var)` emitting
> raw float bits, a global shadowing a function parameter (which had been driving the DHT
> start pulse for 250 ms instead of 18), `millis()` counting 1024 ms per second, and a
> timer's second PWM channel disconnecting the first. Each fix shipped with a regression
> test. Suites at that release: **517 unit, 508 driver, 1549 AVR integration**.
>
> **Alpha 10 still has bugs, and the hunt did not stop.** 145 more fixes have landed since
> it shipped, most of them turning a case the compiler used to answer blindly into a
> diagnostic that names what it cannot do. That work is heading for **beta 1**; watch the
> repo if you want to hear when it lands.
>
> So: core compilation is stable and test-covered, the alpha is usable, and you will still
> find edges. If you hit one, [please open an issue](https://github.com/PyMCU/PyMCU/issues) —
> that is how the list above got written.
>
> **Avoid `pymcu.hal.*`** during the alpha — the native HAL API may change between releases.
> Use the **MicroPython** or **CircuitPython** compat API instead; those are stable and
> community-specified.

PyMCU compiles a **statically-typed subset of Python** into bare-metal firmware for
**AVR, ARM (RP2040 / RP2350) and PIC** — no runtime, no interpreter, no virtual machine.
The same binary you would write in C.

<p align="center">
  <img src="https://raw.githubusercontent.com/PyMCU/PyMCU/main/docs/_static/images/blink-demo.gif" alt="PyMCU demo: MicroPython-flavoured blink compiled to 150 bytes and flashed to an Arduino Uno" width="860">
</p>

<p align="center"><em>A real session: 9 lines of Python &rarr; <code>pymcu build</code> &rarr; <strong>150 bytes of flash</strong> &rarr; running on an Arduino Uno.
Then the delay is edited, rebuilt and reflashed &mdash; the whole loop takes seconds.</em></p>

---

## The pitch in one table

LED blink for ATmega328P @ 16 MHz — all variants do the same thing:
configure PB5 as output, then loop `LED on → wait 500 ms → LED off → wait 500 ms` forever.

| Source | **Total flash** | SRAM |
|---|---|---|
| **C** (`avr-gcc -Os`) | 176 B | 0 B |
| **PyMCU** (native HAL) | **150 B** | 0 B |
| **PyMCU** (MicroPython API) | **150 B** | 0 B |
| **PyMCU** (CircuitPython API) | **152 B** | 0 B |
| **Arduino** (IDE defaults) | 924 B | 9 B |

PyMCU produces a **smaller binary than C** here. Why?

`Pin("PB5", Pin.OUT)` and `delay_ms(500)` are resolved entirely at compile time — the
compiler sees through the Python objects and emits the same raw `SBI`/`CBI` port-toggle
instructions a C programmer would write by hand. The rest of the difference is the delay:
PyMCU emits one calibrated delay subroutine shared by both waits (`rcall` twice), where
avr-libc's `_delay_ms` is inlined at each call site — and there is no `call main` / `jmp _exit`
scaffolding around the program.
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
import time

led = digitalio.DigitalInOut(board.LED)
led.direction = digitalio.Direction.OUTPUT

while True:
    led.value = True
    time.sleep(0.5)
    led.value = False
    time.sleep(0.5)
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
pymcu new blink --board arduino_uno --stdlib micropython
cd blink
```

That is the whole setup. `pymcu new` scaffolds a project that already builds: a
`pyproject.toml` carrying the board, frequency, toolchain and dependencies, a
`src/main.py` with a working blink, plus `requirements.txt`, a `Makefile` and VS Code
tasks. Pass `--stdlib circuitpython` for the other API, or run it with no flags and it
asks for what you left out.

> [!WARNING]
> `pymcu new` asks whether to install dependencies and **defaults to no**. The compat
> layer you picked is one of those dependencies, so declining leaves you with a project
> that scaffolds fine and a first `pymcu build` that fails with
> `ImportError: Module not found: machine`. If you already said no, install them from
> inside the project with `uv sync`, `poetry install` or `pip install -e .`.

### 3. The program it wrote for you

**MicroPython style** (`--stdlib micropython`):

```python
# src/main.py
from machine import Pin
from time import sleep_ms

led = Pin(13, Pin.OUT)
while True:
    led.value(1)
    sleep_ms(500)
    led.value(0)
    sleep_ms(500)
```

**CircuitPython style** (`--stdlib circuitpython`):

```python
# src/main.py
import board
import digitalio
import time

led = digitalio.DigitalInOut(board.LED)
led.direction = digitalio.Direction.OUTPUT
while True:
    led.value = True
    time.sleep(0.5)
    led.value = False
    time.sleep(0.5)
```

Both compile to bare-metal firmware for the same chip. The
[Quick Start](https://docs.pymcu.org/getting-started/quickstart/) walks the same path in
more detail, including what each scaffolded file is for.

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
| **PIC** (mid-range) | PIC16F84A, PIC16F877A |

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
| `pymcu.hal.power` | `sleep_idle` / `sleep_adc_noise` / `sleep_power_down` / `sleep_power_save` / `sleep_standby` / `sleep_extended_standby` |

Drivers: DHT11, DS18B20, HD44780 LCD, SSD1306 OLED, MAX7219 8x8 matrix, BMP280, WS2812 NeoPixel.

---

## What Python features are supported

PyMCU accepts Python syntax but enforces a strict compile-time type system.

**Supported:**
- Integer types: `uint8`, `int8`, `uint16`, `int16`, `uint32`, `int32`, `float` — with
  type inference for unannotated `def` parameters and returns
- Fixed arrays `buf: uint8[16]`, `bytearray`, heap-bounded lists `x: list[uint8] = list()`
- Slices: equal-length assignment (including through `__setitem__`, so
  `microcontroller.nvm[0:4] = b"..."` compiles) and iteration with runtime bounds
  (`for b in buf[0:n]`)
- `print()` of a `bytearray` or a slice as the CPython repr, and of a `float` with two
  rounded decimals; `s = "".join([chr(b) for b in buf])` for bytes-to-string
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
- Anything whose *size* is only known at runtime: a slice read bound to a name
  (`b = buf[0:n]`), a runtime tuple, a comprehension filtered on a runtime condition

The compiler rejects unsupported features with a clear error at compile time — including the
ones the hardware cannot honour, such as a runtime pin number, an image larger than the
chip's flash, or static data that does not fit in SRAM.
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

## Written up elsewhere

- [Python on a Classic Uno](https://projecthub.arduino.cc/begeistert/python-on-a-classic-uno-38-bytes-of-code-zero-ram-534299) on Arduino Project Hub — why the board that taught millions of us electronics never got to speak Python, and what changes once the interpreter is gone.

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
