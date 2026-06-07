# Quick Start

This guide walks through creating a PyMCU project from scratch, building it, and flashing to an
Arduino Uno (ATmega328P).

## 1. Create a new project

```bash
pymcu new blink_led
cd blink_led
```

`pymcu new` prompts you to choose a target chip and sets up:

- `src/main.py` — entry-point source file
- `pyproject.toml` — project configuration
- `.vscode/tasks.json` — VS Code build/flash tasks
- `.gitignore`

The generated `pyproject.toml` for Arduino Uno:

```toml
[tool.pymcu]
chip      = "atmega328p"
frequency = 16000000

[tool.pymcu.programmer]
name     = "avrdude"
protocol = "arduino"
baudrate = 115200
```

## 2. Write your program

Edit `src/main.py`:

```python
from pymcu.hal.gpio import Pin
from pymcu.time import delay_ms

def main():
    led = Pin("PB5", Pin.OUT)   # Arduino Uno built-in LED (D13)
    while True:
        led.toggle()
        delay_ms(500)
```

:::{note}
Every variable **must** have a type annotation. `Pin.OUT`, `Pin.IN`, etc. are compile-time
integer constants — no runtime class exists.
:::

## 3. Build

```bash
pymcu build
```

Output:

```
[pymcu] Compiling src/main.py → dist/firmware.hex
[pymcu] Flash: 124 bytes  SRAM: 0 bytes  (ATmega328P)
```

The compiled `.hex` file is in `dist/firmware.hex`.

## 4. Flash

Connect your Arduino Uno via USB, then:

```bash
pymcu flash --port /dev/cu.usbmodem*   # macOS
pymcu flash --port /dev/ttyACM0        # Linux
pymcu flash --port COM3                # Windows
```

The LED on pin 13 should blink at 1 Hz.

---

## What just happened?

Unlike MicroPython or CircuitPython, PyMCU did **not** upload an interpreter to your Arduino.
The compiler on your PC translated your Python source directly to AVR machine code. The MCU
runs that code at full clock speed — no Python runtime exists on the device.

---

## Next steps

- {doc}`../language/overview` — understand the compiled model and type system
- {doc}`../library/gpio` — full GPIO / Pin reference
- {doc}`../examples/index` — annotated firmware examples
