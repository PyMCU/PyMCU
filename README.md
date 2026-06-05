# PyMCU — Python to bare-metal firmware

> **Alpha — v0.1.** Core AVR compilation is solid and test-covered.
> Error messages, tooling, and some language edges are still rough.

PyMCU compiles a **statically-typed subset of Python** into bare-metal AVR firmware —
no runtime, no interpreter, no virtual machine. The same binary you would write in C.

---

## The pitch in one table

LED blink for ATmega328P @ 16 MHz — all variants perform the identical operation:
set PB5 as output, then loop `set PB5 high → delay 500 ms → set PB5 low → delay 500 ms`.

Every binary is made of three fixed layers. The table shows each one separately:

| Source | Preamble | Startup | User code | **Total** | SRAM |
|---|---|---|---|---|---|
| **C** (`avr-gcc -Os`) | 108 B | 24 B | **44 B** | 176 B | 0 B |
| **PyMCU** (native HAL) | 106 B | 24 B | **32 B** | 162 B | 0 B |
| **PyMCU** (MicroPython API) | 106 B | 24 B | **32 B** | 162 B | 0 B |
| **PyMCU** (CircuitPython API) | 106 B | 24 B | **34 B** | 164 B | 0 B |
| **Arduino** (IDE defaults) | — | — | — | ~1 024 B | 9 B |

**Layer 1 — Preamble** (constant, not program logic):
C emits 26 × 4-byte `JMP` slots + `__bad_interrupt: JMP` = **108 B**.
PyMCU emits 26 × (`RJMP` + `NOP`) + `__bad_interrupt: RJMP` = **106 B** (`RJMP` is 2-byte vs 4-byte `JMP`; a `NOP` pad keeps slot alignment for future ISR use). Both give equivalent safety coverage.

**Layer 2 — Startup** (fixed per toolchain, not per program):
C `__ctors_end` does: `CLR R1` + `OUT SREG, R1` + `LDI/OUT SP` + `CALL main` = **24 B**.
PyMCU does the same: `CLR R1` + `LDI/OUT SP` + `LDI Y` (frame-pointer) = **24 B**.
The BSS-zeroing loop (`LDI Z, _bss_end` + `ST Z+, R1` + `BRNE`) is **only emitted when `_bssSize > 0`** — i.e. when the program actually has runtime global variables that need to be zeroed at boot. This blink has none: unused stdlib globals (`pymcu.exceptions.*`, `_millis_count`) are removed by the **Dead Global Elimination** pass in the optimizer before codegen runs.

**Layer 3 — User code** (the only part that changes between variants):
PyMCU HAL and MicroPython compile to **32 B** of application logic — **12 B less than C's 44 B**.
Constant-folding resolves `Pin("PB5", Pin.OUT)` entirely at compile time and the delay loop counter fits in one fewer register pair compared to the gcc-generated version.
The CircuitPython extra 2 B come from `Direction.setter` clearing PORT before asserting DDR — semantically required by the CircuitPython spec.

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
pipx install "pymcu[avr]"
```

Requires Python 3.11+ and `pipx`. The `[avr]` extra includes the AVR toolchain.

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
dependencies = ["pymcu", "pymcu-circuitpython"]

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
dependencies = ["pymcu", "pymcu-micropython"]

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
| `pymcu.hal.*` | Direct register-level HAL — lowest overhead | included in `pymcu` |

**Start with the compat layer that matches your background.** The APIs are stable,
community-specified, and unlikely to change between alpha releases. Switch to
`pymcu.hal.*` only if you need direct register access or a chip not yet covered.

---

## Supported targets

| Architecture | Chips |
|---|---|
| **AVR** (ATmega) | ATmega48/88/168/328P, ATmega2560, ATmega32U4 |
| **AVR** (ATtiny) | ATtiny25/45/85, ATtiny24/44/84, ATtiny13/13A, ATtiny2313/4313 |
| **PIC** | PIC12, PIC14, PIC14E, PIC18 |
| **RISC-V** | Experimental |

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
