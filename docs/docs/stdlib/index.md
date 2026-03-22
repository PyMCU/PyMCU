# Standard Library Overview

Whisnake ships a platform-abstracted stdlib that compiles to zero-overhead assembly on each
supported architecture. Every module uses `@inline` ZCA wrappers — there is no runtime library
linked into your firmware.

## Module Index

| Module | Import | Purpose |
|---|---|---|
| `whisnake.hal.gpio` | `from whisnake.hal.gpio import Pin` | Digital I/O |
| `whisnake.hal.uart` | `from whisnake.hal.uart import UART` | Serial communication |
| `whisnake.hal.adc` | `from whisnake.hal.adc import AnalogPin` | Analog-to-digital conversion |
| `whisnake.hal.timer` | `from whisnake.hal.timer import Timer0` | Hardware timer |
| `whisnake.hal.pwm` | `from whisnake.hal.pwm import PWM` | PWM output |
| `whisnake.hal.spi` | `from whisnake.hal.spi import SPI` | SPI bus |
| `whisnake.hal.i2c` | `from whisnake.hal.i2c import I2C` | I2C / TWI bus |
| `pymcu.time` | `from whisnake.time import delay_ms` | Busy-wait delays |
| `pymcu.drivers.dht11` | `from pymcu.drivers.dht11 import DHT11` | DHT11 sensor |
| `pymcu.boards.arduino_uno` | `from pymcu.boards.arduino_uno import D13` | Arduino Uno pin names |
| `pymcu.types` | `from whisnake.types import uint8, ptr` | Type aliases |

## Architecture Support Matrix

| Module | AVR (ATmega328P) | PIC14/14E | PIC18 | RISC-V |
|---|---|---|---|---|
| GPIO | Complete | Complete | Complete | Partial |
| UART | Complete | Complete | Complete | Partial |
| ADC | Complete | Complete | Complete | — |
| Timer | Complete | Complete | Complete | — |
| PWM | Complete | Complete | Complete | — |
| SPI | Complete | — | — | — |
| I2C | Complete | — | — | — |
| DHT11 | Complete | — | — | — |
