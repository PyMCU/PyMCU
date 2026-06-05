# PyMCU — Python to bare-metal firmware

> [!IMPORTANT]
> **Alpha — v0.1 — under active development.**
> Core AVR compilation is stable and test-covered, but rough edges remain in error messages
> and tooling. Stability is expected for the supported feature set — if you hit a bug,
> [please open an issue](https://github.com/begeistert/PyMCU/issues), it helps a lot.
>
> **Avoid `pymcu.hal.*`** during the alpha — the native HAL API may change between releases.
> Use the **MicroPython** or **CircuitPython** compat API instead; those are stable and
> community-specified.

PyMCU compiles a **statically-typed subset of Python** into bare-metal AVR firmware —
no runtime, no interpreter, no virtual machine. The same binary you would write in C.

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
> hand-written C. That is still 100-1000x smaller than any embedded Python interpreter.

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
pipx install "pymcu-compiler[avr]"
```

Requires Python 3.11+ and `pipx`. The `[avr]` extra includes the AVR toolchain.

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
dependencies = ["pymcu-compiler", "pymcu-circuitpython"]

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
dependencies = ["pymcu-compiler", "pymcu-micropython"]

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
- Integer types: `uint8`, `int8`, `uint16`, `int16`, `uint32`, `int32`, `float`
- Fixed arrays `buf: uint8[16]` and heap-bounded lists `x: list[uint8] = list()`
- `for`, `while`, `if`, `match / case`, `with`, `class`, `@inline`, `lambda`
- `@interrupt` ISR handlers, `asm("...")` inline assembly
- `try / except / raise / finally` (AVR only; `raise` and `except` in same function)
- CircuitPython and MicroPython compat packages

**Not supported:**
- `dict` / `set` (hash tables require heap)
- Runtime `f"value={x}"` with non-constant expressions (compile-time constants only)
- `async` / `await` — use `@interrupt` + polling loop instead
- Closures capturing mutable variables — use explicit parameters
- `*args` / `**kwargs`

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
consider sponsoring the project — the goal is $200-300/month, which nets roughly
$100-200 after payment processing and local taxes, enough to cover the AI tooling
costs that made this first release possible.

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
