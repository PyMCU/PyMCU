# neopixel

Cycle a single **WS2812B** (NeoPixel) through red, green, and blue.

## What it does

Drives one WS2812B on **PD6** using the stdlib `NeoPixel` driver, cycling colors
every 500 ms and reporting the phase over UART. Because WS2812B timing is
bit-banged at 800 kHz, interrupts are disabled only during the pixel write +
`show()` to guarantee correct timing.

## Hardware

- Arduino Uno @ 16 MHz
- WS2812B data in on **PD6** (Arduino pin 6)
- Serial terminal at **9600 baud**

## Key concepts

- `pymcu.drivers.neopixel.NeoPixel` — `set_pixel(r, g, b)`, `show()`
- `disable_interrupts()` / `enable_interrupts()` around timing-critical code

## Build & flash

```bash
cd examples/avr/neopixel
pymcu build
pymcu flash --port /dev/cu.usbmodemXXXX
```
