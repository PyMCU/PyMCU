# pwm-fade

Smoothly fade an LED in and out with hardware PWM.

## What it does

Uses Timer0 Fast PWM on **PD6 (OC0A)** to ramp the duty cycle 0 → 255 → 0
continuously, producing a breathing-LED effect. A `match`/`case` on the
direction flag handles the up/down ramp with `+= 1` / `-= 1`.

## Hardware

- Arduino Uno @ 16 MHz
- LED (with resistor) on **PD6** (Arduino pin 6, OC0A)

## Key concepts

- `pymcu.hal.pwm.PWM` — `start()`, `set_duty()`
- Augmented assignment and direction state machine

## Build & flash

```bash
cd examples/avr/pwm-fade
pymcu build
pymcu flash --port /dev/cu.usbmodemXXXX
```
