# max7219

Drive a **MAX7219** 8x8 LED matrix over SPI.

## What it does

Initializes a MAX7219 matrix via the stdlib driver, writes a checkerboard
pattern across all 8 rows, sets the brightness, then animates a scrolling
pattern in the main loop.

## Hardware

- Arduino Uno @ 16 MHz
- MAX7219 module:
  - **MOSI → PB3**, **SCK → PB5**, **CS → PB2**
- Serial terminal at **9600 baud**

## Expected output

```
MAX7219
OK
```

## Key concepts

- `pymcu.hal.spi.SPI(cs="PB2")` with a custom chip-select pin
- `pymcu.drivers.max7219.MAX7219` — `init()`, `clear()`, `set_row()`, `set_brightness()`

## Build & flash

```bash
cd examples/avr/max7219
pymcu build
pymcu flash --port /dev/cu.usbmodemXXXX
```
