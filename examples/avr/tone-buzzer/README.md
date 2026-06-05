# tone-buzzer

Play a melody on a passive buzzer with zero-CPU hardware tone generation.

## What it does

Plays a repeating C-major scale (C4–C5) on a passive buzzer using Timer2 CTC
with hardware pin toggle — no CPU cycles are spent during tone playback. The
tone pin is hardwired to **OC2A (PB3 / D11)**.

## Hardware

- Arduino Uno @ 16 MHz
- Passive buzzer between **D11 (PB3)** and GND

## Key concepts

- `pymcu.hal.tone` — `tone(freq)` / `noTone()`
- Hardware-toggle tone generation via Timer2 CTC

## Build & flash

```bash
cd examples/avr/tone-buzzer
pymcu build
pymcu flash --port /dev/cu.usbmodemXXXX
```
