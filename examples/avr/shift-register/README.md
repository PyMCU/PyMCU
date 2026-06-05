# shift-register

Bit-banged 74HC595 shift register driving a running light.

## What it does

Manually clocks 8 bits (MSB first) into a 74HC595 to animate a single lit bit
rotating across 8 LEDs. Exercises the variable-amount right-shift codegen path
(`pattern >> bit` with a non-constant `bit`), MSB extraction, and rotation
arithmetic. The current pattern is also sent over UART.

## Hardware

- ATmega328P + 74HC595 @ 16 MHz
  - **SER (14) → PB0**, **SRCLK (11) → PB1**, **RCLK (12) → PB2**
  - OE (13) → GND, MR (10) → VCC
  - Q0–Q7 → 8 LEDs with resistors
- Serial terminal at **9600 baud**

## Key concepts

- Bit-banging a shift-register protocol with `Pin`
- Variable-amount shifts and bit rotation

## Build & flash

```bash
cd examples/avr/shift-register
pymcu build
pymcu flash --port /dev/cu.usbmodemXXXX
```
