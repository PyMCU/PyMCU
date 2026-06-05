# random-led

Random blink intervals seeded from ADC noise.

## What it does

Reads a floating ADC pin for hardware noise to seed the PRNG, then blinks the
built-in LED with random on/off intervals, printing the chosen delays over UART.
Shows `map_range()` and `constrain()` from `pymcu.math` plus `pymcu.random`.

## Hardware

- Arduino Uno @ 16 MHz
- Built-in LED on **PB5** (D13)
- (ADC channel 0 left floating for entropy)
- Serial terminal at **9600 baud**

## Key concepts

- `randomSeed()` / `random()` PRNG
- `pymcu.math.map_range()` and `constrain()`

## Build & flash

```bash
cd examples/avr/random-led
pymcu build
pymcu flash --port /dev/cu.usbmodemXXXX
```
