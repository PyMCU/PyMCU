# multi-pin

Six LEDs and two buttons — a small interactive light pattern.

## What it does

Six LEDs on **PB0–PB5** and two buttons on **PD2/PD3**. Button A advances a step
counter (wrapping 0–5), button B resets it to 0. A `match`/`case` lights exactly
one LED per step. The current step is sent over UART.

## Hardware

- Arduino Uno @ 16 MHz
- LEDs (with resistors) on **PB0–PB5**
- Button A on **PD2**, Button B on **PD3** (active-low, pull-up)
- Serial terminal at **9600 baud**

## Key concepts

- Many `Pin` instances at once
- Edge detection per button
- `match`/`case` dispatch

## Build & flash

```bash
cd examples/avr/multi-pin
pymcu build
pymcu flash --port /dev/cu.usbmodemXXXX
```
