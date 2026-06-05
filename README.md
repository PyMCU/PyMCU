# PyMCU — Python to bare-metal firmware

> **Alpha — v0.1.** Core AVR compilation is solid and test-covered.
> Error messages, tooling, and some language edges are still rough.

PyMCU compiles a **statically-typed subset of Python** into bare-metal AVR firmware —
no runtime, no interpreter, no virtual machine. The same binary you would write in C.

---

## The pitch in one table

LED blink for ATmega328P @ 16 MHz — all variants perform the identical operation:
set PB5 as output, then loop `set PB5 high → delay 500 ms → set PB5 low → delay 500 ms`.
Flash sizes exclude the interrupt-vector preamble, which is constant across all programs.

| Source | Flash | SRAM | Notes |
|---|---|---|---|
| **C** (`avr-gcc -Os`) | **68 bytes** | 0 bytes | 176b total − 108b preamble (26×`JMP` + `__bad_interrupt`) |
| **PyMCU** (CircuitPython API) | **80 bytes** | 0 bytes | `led.value = True/False`; 186b total − 106b preamble |
| **PyMCU** (MicroPython API) | **78 bytes** | 0 bytes | `led.value(1/0)`; 184b total − 106b preamble |
| **PyMCU** (native HAL) | **78 bytes** | 0 bytes | `led.high()/led.low()`; 184b total − 106b preamble |
| **Arduino** (IDE defaults) | **~924 bytes** | 9 bytes | Full framework overhead (estimated) |

*Preamble definition: C uses 26×4-byte `JMP` slots + `__bad_interrupt: JMP` = 108 B;
PyMCU uses 26×(`RJMP`+NOP) + `__bad_interrupt: RJMP` = 106 B.
Both are equivalent in safety coverage.
The "after preamble" totals include crt0-compatible startup: `CLR R1` (zero-register
invariant for C FFI), SP/Y-register setup, and a BSS-zeroing loop that ensures globals
are clean after a watchdog reset (36 B constant; C startup is 24 B).
Stripping startup overhead leaves **42 B** of user logic for PyMCU HAL/MP vs **44 B** for C
for this set/clear + delay loop — constant folding through property chains works correctly
and PyMCU generates 5 % fewer bytes of application code.
The extra 2 B in the CircuitPython variant (44 B) come from `Direction.setter` clearing
the PORT pin before asserting DDR — semantically correct and expected.*

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
pymcu build   # → dist/firmware.hex  (78 bytes flash, 0 bytes SRAM)
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
