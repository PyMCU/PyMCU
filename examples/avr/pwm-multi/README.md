# pwm-multi

Three independent hardware PWM channels running at once.

## What it does

Runs Fast PWM on three timers simultaneously, each with a different starting
phase:

- **PD6 (OC0A, Timer0)** — ramp from 0
- **PB3 (OC2A, Timer2)** — phase offset 128
- **PB1 (OC1A, Timer1)** — phase offset 64

All duty cycles advance together in the main loop; `D\n` is sent over UART once
per full cycle wrap.

## Hardware

- Arduino Uno @ 16 MHz
- LEDs on **PD6** (pin 6), **PB3** (pin 11), **PB1** (pin 9)
- Serial terminal at **9600 baud**

## Key concepts

- Multiple timers driving independent PWM outputs

## Build & flash

```bash
cd examples/avr/pwm-multi
pymcu build
pymcu flash --port /dev/cu.usbmodemXXXX
```
