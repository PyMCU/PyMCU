# spi-shift-register

Hardware SPI driving a 74HC595 with selectable animations.

## What it does

Clocks bytes to a 74HC595 using hardware SPI (much faster than bit-banging) to
animate LEDs. Three animation modes cycle on any UART input:

- `0` running light (single bit rotating)
- `1` chaser pair (two adjacent bits)
- `2` binary counter (0x00 → 0xFF)

## Hardware

- Arduino Uno + 74HC595 @ 16 MHz
  - **SER (14) ← MOSI PB3**, **SRCLK (11) ← SCK PB5**, **RCLK (12) ← SS PB2**
  - OE (13) → GND, MR (10) → VCC
  - Q0–Q7 → 8 LEDs with resistors
- Serial terminal at **9600 baud** (send any byte to change mode)

## Key concepts

- `with spi:` for select/deselect around `spi.write()`
- Non-blocking UART poll via `UCSR0A[7]` (RXC0)

## Build & flash

```bash
cd examples/avr/spi-shift-register
pymcu build
pymcu flash --port /dev/cu.usbmodemXXXX
```
