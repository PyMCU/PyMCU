# soft-pwm

Software PWM driven by a timer overflow ISR.

## What it does

A Timer0 overflow ISR (~4.1 ms) sets a `GPIOR0` flag. The main loop keeps a
0–99 counter and turns the LED on while `counter < duty`. The duty cycle bounces
through 0 → 25 → 50 → 75 → 100 → 75 → … every 100 ticks, demonstrating PWM
without a dedicated PWM output pin.

## Hardware

- Arduino Uno @ 16 MHz
- LED on **PB5** (D13, built-in)
- Serial terminal at **9600 baud**

## Key concepts

- Timer overflow ISR + `GPIOR0` flag
- Software PWM via a duty-cycle counter
- `match`/`case` lookup table (`duty_value()`)

## Build & flash

```bash
cd examples/avr/soft-pwm
pymcu build
pymcu flash --port /dev/cu.usbmodemXXXX
```
