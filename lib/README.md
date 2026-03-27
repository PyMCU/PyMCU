# pymcu-stdlib

This package contains the standard library and HAL definitions for **PyMCU**, a Python-to-microcontroller toolchain that compiles a statically-typed subset of Python directly to bare-metal machine code.

These files provide hardware register mappings, HAL abstractions, and phantom types that allow Python IDEs (PyCharm, VS Code, etc.) to provide autocompletion and type checking for microcontroller code, while being compiled by the `pymcuc` compiler.

## Purpose

Microcontroller code written for PyMCU uses specialized types and access patterns that are not native to standard Python. This package bridges that gap by:

1. **Defining Hardware Registers**: Mapping register names (e.g., `PORTB`, `DDRB`) to their physical memory addresses for each supported chip.
2. **Providing Type Safety**: Using phantom types like `uint8`, `uint16`, and `ptr[T]` to ensure correct data sizing at compile time.
3. **HAL Abstractions**: Zero-cost abstractions (`Pin`, `UART`, `ADC`, `PWM`, `SPI`, `I2C`, `EEPROM`, `Watchdog`, `Power`) that inline to chip-specific code at compile time.
4. **Enabling IDE Features**: Allowing "Go to Definition", autocompletion, and static analysis while writing firmware in Python.

## Supported Chips

- **AVR ATmega family**: ATmega48/48P, ATmega88/88P, ATmega168/168P, ATmega328/328P (Arduino Uno)
- **AVR ATtiny (PORTB only)**: ATtiny85, ATtiny45, ATtiny25, ATtiny13, ATtiny13a
- **AVR ATtiny (PORTA+PORTB)**: ATtiny84, ATtiny44, ATtiny24
- **AVR ATtiny (PORTD+PORTB)**: ATtiny2313, ATtiny4313
- **PIC**: PIC14 (PIC16F84A, PIC16F877A), PIC14E (PIC16F18877), PIC18 (PIC18F45K50)
- **RISC-V**: CH32V003 (experimental)

## Installation

```bash
uv pip install pymcu
```

Or with pipx for CLI use:

```bash
pipx install pymcu
```

## Usage

Import chip registers and HAL modules in your firmware source:

```python
from pymcu.chips.atmega328p import PORTB, DDRB
from pymcu.types import uint8
from pymcu.hal.gpio import Pin

led = Pin("PB5", Pin.OUT)

while True:
    led.toggle()
```

For ATtiny targets:

```python
from pymcu.chips.attiny85 import PORTB, DDRB
from pymcu.types import uint8
from pymcu.hal.gpio import Pin

led = Pin("PB0", Pin.OUT)

while True:
    led.toggle()
```

> **Note**: These files are stubs. Running them directly with a standard Python interpreter will raise `RuntimeError` if you attempt to access hardware registers — they lack the physical hardware interface and must be compiled with the `pymcuc` toolchain to run on a microcontroller.

## Structure

```
lib/src/pymcu/
  chips/          -- chip-specific register definitions
    atmega328p.py
    attiny85.py
    attiny84.py
    attiny2313.py
    ...
  hal/            -- zero-cost hardware abstractions
    gpio.py       -- Pin (digital I/O)
    uart.py       -- UART
    adc.py        -- ADC
    pwm.py        -- PWM
    spi.py        -- SPI (hardware)
    softspi.py    -- SPI (bit-bang)
    i2c.py        -- I2C
    eeprom.py     -- EEPROM
    watchdog.py   -- Watchdog timer
    power.py      -- Sleep / power modes
    _gpio/        -- chip-specific GPIO backend implementations
    _uart/        -- chip-specific UART backend implementations
    ...
  types.py        -- ptr[T], uint8, uint16, int8, inline, const, asm, ...
  math/           -- software math routines (division, etc.)
```

## License

MIT License. See [LICENSE](../LICENSE) for details.
