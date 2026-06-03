# PyMCU — Python to bare-metal firmware

> **Alpha — v0.1.** Core AVR compilation is solid and test-covered.
> Error messages, tooling, and some language edges are still rough.

PyMCU compiles a **statically-typed subset of Python** into bare-metal AVR firmware —
no runtime, no interpreter, no virtual machine. The same binary you would write in C.

---

## The pitch in one table

| Target | Blink flash footprint | SRAM | Notes |
|---|---|---|---|
| **Pure C** (`avr-gcc -Os`) | ~68 bytes | 0 bytes | Hand-written register access |
| **PyMCU** (native HAL) | ~72 bytes | 0 bytes | Python source → same output |
| **PyMCU** (CircuitPython API) | 124 bytes | 0 bytes | Full CP compat layer |
| **Arduino** (IDE defaults) | ~924 bytes | 9 bytes | Includes full Arduino runtime |

Python syntax. C-equivalent output.

---

## What it looks like

### Your source (Python)

```python
from pymcu.hal.gpio import Pin
from pymcu.time import delay_ms

led = Pin("PB5", Pin.OUT)

while True:
    led.toggle()
    delay_ms(500)
```

### What the compiler produces (AVR assembly, annotated)

```asm
main:
    SBI  0x04, 5       ; DDRB |= (1 << PB5)  — output mode
loop:
    SBI  0x05, 5       ; PORTB |= (1 << PB5) — LED on
    CALL _delay_ms_500
    CBI  0x05, 5       ; PORTB &= ~(1 << PB5) — LED off
    CALL _delay_ms_500
    RJMP loop
```

No heap. No runtime. No surprises.

---

## Or use the CircuitPython API you already know

```python
import board
import digitalio
from time import sleep_ms

led = digitalio.DigitalInOut(board.LED)
led.direction = digitalio.Direction.OUTPUT

while True:
    led.value = not led.value
    sleep_ms(500)
```

```bash
pymcu build   # → dist/firmware.hex  (124 bytes flash, 0 bytes SRAM)
pymcu flash   # → avrdude upload to Arduino Uno
```

The same code that runs under CircuitPython on a Pico compiles to bare-metal AVR firmware
with zero runtime overhead.

---

## First binary in under 5 minutes

### 1. Install

```bash
pipx install "pymcu[avr]"
```

Requires Python 3.11+ and `pipx`. The `[avr]` extra includes the AVR toolchain and backend.

### 2. Create a project

```bash
pymcu new blink
cd blink
```

### 3. Write your program

```python
# src/main.py
from pymcu.hal.gpio import Pin
from pymcu.time import delay_ms

led = Pin("PB5", Pin.OUT)

while True:
    led.toggle()
    delay_ms(500)
```

### 4. Configure the target

```toml
# pyproject.toml  (already created by pymcu new)
[tool.pymcu]
board     = "arduino_uno"
frequency = 16000000
```

### 5. Build and flash

```bash
pymcu build
# Compiling src/main.py...
# → dist/firmware.hex  (72 bytes flash, 0 bytes SRAM)

pymcu flash --port /dev/cu.usbmodem*
# avrdude: 72 bytes of flash verified
```

Your LED is blinking. Total time: under 3 minutes on a clean machine.

---

## Supported targets

| Architecture | Chips |
|---|---|
| **AVR** | ATmega48/88/168/328P, ATmega2560, ATmega32U4 |
| **AVR tiny** | ATtiny25/45/85, ATtiny24/44/84, ATtiny13/13A, ATtiny2313/4313 |
| **PIC** | PIC12, PIC14, PIC14E, PIC18 |
| **RISC-V** | Experimental |

---

## API choices

Start with a compatibility layer if you are new to PyMCU — the APIs are stable,
community-specified, and port directly from existing MicroPython or CircuitPython projects.

| Package | API | Best for |
|---|---|---|
| `pymcu-circuitpython` | `digitalio`, `analogio`, `busio`, `pwmio`, `time`, `board` | Port CircuitPython code directly |
| `pymcu-micropython` | `machine`, `utime` | Port MicroPython code directly |
| `pymcu.hal.*` | Direct register-level HAL | Full control, lowest overhead |

---

## What Python features are supported

PyMCU accepts Python syntax but enforces a strict compile-time type system.
These features **work**:

- Integer types: `uint8`, `int8`, `uint16`, `int16`, `uint32`, `int32`, `float`
- Fixed arrays: `buf: uint8[16]`
- Heap-bounded lists: `x: list[uint8] = list()`
- `for`, `while`, `if`, `match / case`, `with`, `class`, `@inline`
- `@interrupt` for hardware ISR handlers
- `try / except / raise / finally` (AVR only; `raise` and `except` in same function)
- `asm("...")` inline assembly
- CircuitPython and MicroPython compat packages

These features **do not exist**:

- `dict` / `set` (hash tables require heap)
- Runtime string formatting — `f"value={x}"` with non-constant expressions
- `async` / `await` (use `@interrupt` + polling loop)
- Closures capturing mutable variables
- `*args` / `**kwargs`

The compiler rejects unsupported features with a clear error at compile time.
See the [Language Limitations](docs/language/limitations.md) page for the full list.

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
| `pymcu.hal.power` | `sleep_idle` / `power_down` / `standby` / … |

Drivers: DHT11, DS18B20, LM35, HD44780 LCD, SSD1306 OLED, MAX7219, BMP280, WS2812 NeoPixel.

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
