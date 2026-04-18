# pymcu-toolchain-avr

AVR toolchain plugin for PyMCU. Provides the GNU AVR binutils toolchain (avr-as, avr-gcc, avr-objcopy) for assembling and linking PyMCU firmware for AVR targets (ATmega, ATtiny, etc.).

## Installation

```bash
pip install pymcu[avr]
# or independently:
pip install pymcu-toolchain-avr
```

## Supported chips

Any AVR chip matching the `at(mega|tiny|xmega|90)` prefix, e.g.:
- `atmega328p`, `atmega2560`, `atmega32u4`
- `attiny85`, `attiny84`, `attiny13`

## Features

- Auto-downloads pre-built avr-gcc toolchain from [ZakKemble/avr-gcc-build](https://github.com/ZakKemble/avr-gcc-build)
- Supports plain assembly builds and C/C++ interop (`@extern`)
- Includes legacy `AvraToolchain` for opt-in use

## Plugin registration

This package registers itself under `pymcu.toolchains` so the PyMCU CLI discovers it automatically:

```
pymcu toolchain list
pymcu toolchain install avr
```
