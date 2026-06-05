# state-machine

A UK-style traffic-light finite state machine.

## What it does

A `TrafficLight` zero-cost class encapsulates the three LED pins and exposes a
`state` property setter that drives the correct LEDs. The FSM in `main()` cycles
RED → RED+YELLOW → GREEN → YELLOW using a Timer0-overflow software clock
(244 overflows ≈ 1 s) and a `match`/`case` transition table. Each transition is
announced over UART.

## Hardware

- Arduino Uno @ 16 MHz
- Red LED on **PB0**, Yellow on **PB1**, Green on **PB2** (with resistors)
- Serial terminal at **9600 baud**

## Key concepts

- `@property` / `@state.setter` on a ZCA class
- `match`/`case` FSM dispatch
- Timer0 overflow polling as a 1 Hz time base

## Build & flash

```bash
cd examples/avr/state-machine
pymcu build
pymcu flash --port /dev/cu.usbmodemXXXX
```
