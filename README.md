# PyMCU: Python-to-MCU Compiler

> **Alpha — v0.1.0a1.** Core AVR compilation is solid and test-covered.
> Error messages, tooling, and some language edges are still rough.
> Feedback welcome; breakage expected.

PyMCU compiles a **statically-typed, allocation-free subset of Python** into bare-metal
firmware for 8-bit microcontrollers — no runtime, no heap, no interpreter.

Supported targets: AVR (ATmega48/88/168/328 family, ATtiny25/45/85, ATtiny24/44/84,
ATtiny2313/4313, ATtiny13/13a), PIC14/PIC14E, PIC18, and experimental RISC-V.

---

## This is not standard Python

PyMCU accepts Python *syntax* but enforces a strict compile-time type system. These
standard Python features **do not exist** in PyMCU:

- Dynamic containers — `list.append`, `dict`, `set`
- Exception handling — `try/except/finally`
- Runtime string formatting — `f"value={x}"` with non-constant expressions
- Closures, `*args`, `**kwargs`, `lambda`
- Any heap allocation

The compiler will reject unsupported features with a compile-time error.
Refer to the Language Limitations page in the documentation before starting a project.

---

## Choosing an API

PyMCU provides two layers. **Start with a compatibility layer** if you are new to the
project — their APIs are stable, community-specified, and unlikely to change between
alpha releases.

### Compatibility layers (recommended for alpha)

| Package | API surface | Best for |
|---------|-------------|----------|
| `pymcu-circuitpython` | `digitalio`, `analogio`, `busio`, `pwmio`, `time`, `board` | Familiar CircuitPython API; port CP code directly |
| `pymcu-micropython` | `machine`, `time` | Familiar MicroPython API; port uPython code directly |

These layers implement well-established, externally-specified APIs. Breaking changes in
them are unlikely.

### Internal HAL and stdlib (alpha — may change)

The internal `pymcu.hal.*` and `pymcu.chips.*` APIs are lower-level and actively
evolving. Prefer the compat layers above during the alpha period unless you need
direct register access or a chip not yet covered by the compat layers.

---

## Installation

```bash
pipx install "pymcu[avr]"
```

Ensure `pipx` is installed and its bin directory is in your `PATH`. The `[avr]` extra
pulls in the AVR toolchain and backend. Use `[pic]` for PIC targets, `[all]` for both.

---

## Quick start (CircuitPython API)

```toml
# pyproject.toml
[project]
dependencies = ["pymcu", "pymcu-circuitpython"]

[tool.pymcu]
board = "arduino_uno"
frequency = 16000000
```

```python
# src/main.py
import board
import digitalio
from time import sleep_ms

def main():
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

---

## CLI reference

| Command | Description |
|---------|-------------|
| `pymcu new <name>` | Scaffold a new project (interactive) |
| `pymcu build` | Compile `src/` and write `dist/firmware.hex` |
| `pymcu flash` | Flash firmware via the programmer in `pyproject.toml` |
| `pymcu clean` | Remove build artifacts |

---

## Licensing

All components are licensed under the [MIT License](LICENSE).
Your compiled firmware output is entirely yours.

---

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) and [LANGUAGE_ROADMAP.md](LANGUAGE_ROADMAP.md).

---

## Credits

Special thanks to Richard Wardlow, creator of the original
[pyMCU](https://github.com/rwardlow/pyMCU) project (2012).
See [CREDITS.md](CREDITS.md) for the full acknowledgement.
