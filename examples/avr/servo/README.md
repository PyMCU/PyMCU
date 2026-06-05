# servo

Sweep a standard RC servo from 0° to 180° and back.

## What it does

Uses Timer1 Fast PWM (mode 14, 50 Hz, 1–2 ms pulses) via the stdlib `Servo`
driver to continuously sweep a servo on **D9 (OC1A = PB1)**. A second servo on
D10 (`Servo("PB2")`) can run simultaneously with no extra setup.

## Hardware

- Arduino Uno @ 16 MHz
- Servo signal → **D9 (PB1)**; VCC → 5 V; GND → GND
  (use an external 5 V supply for high-torque servos)

## Key concepts

- `pymcu.hal.servo.Servo` — `write(degrees)`
- Hardware 50 Hz PWM for RC servo pulses

## Build & flash

```bash
cd examples/avr/servo
pymcu build
pymcu flash --port /dev/cu.usbmodemXXXX
```
