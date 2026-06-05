# stopwatch

A three-interrupt stopwatch: start/stop, reset, and a timer tick.

## What it does

Runs three simultaneous ISRs:

- **INT0 (PD2)** — start/stop toggle (falling edge)
- **INT1 (PD3)** — reset (falling edge)
- **TIMER0_OVF** — ~16.384 ms tick (61 ticks ≈ 1 second)

The LED is on while running; elapsed seconds are sent over UART as a raw byte +
newline. State is coordinated through `GPIOR0` bit flags.

## Hardware

- Arduino Uno @ 16 MHz
- Start/Stop button on **PD2**, Reset button on **PD3** (active-low, pull-up)
- LED on **PB5** (D13)
- Serial terminal at **9600 baud**

## Key concepts

- Three concurrent interrupt sources
- `GPIOR0` bit flags for ISR ↔ main communication

## Build & flash

```bash
cd examples/avr/stopwatch
pymcu build
pymcu flash --port /dev/cu.usbmodemXXXX
```
