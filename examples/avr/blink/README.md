# blink

The classic "hello world" of embedded — toggle an LED once per second.

## What it does

Configures **PB5** (the Arduino Uno built-in LED on digital pin 13) as an output
and toggles it high/low with a 1 second delay between each state.

This is the best place to start: it exercises the `Pin` HAL, a `while True`
loop, and `delay_ms()`, with no external wiring required.

## Hardware

- Arduino Uno / any ATmega328P board @ 16 MHz
- Built-in LED on **PB5** (digital pin 13) — no wiring needed

## Key concepts

- `Pin("PB5", Pin.OUT)` zero-cost GPIO abstraction (`high()` / `low()`)
- `delay_ms()` busy-wait timing

## Build & flash

```bash
cd examples/avr/blink
pymcu build
pymcu flash --port /dev/cu.usbmodemXXXX
```
