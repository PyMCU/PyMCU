# pcint-counter

Pin Change Interrupt (PCINT0) button counter.

## What it does

A button on **PB0** fires PCINT0 on any edge. `btn.irq(3, pcint0_isr)` sets
`PCMSK0`, `PCICR`, and `SEI` automatically. The ISR sets a `GPIOR0` flag; the
main loop reads PB0 to distinguish a press (low) from a release (high) and only
counts presses, printing `COUNT:NN`.

## Hardware

- Arduino Uno @ 16 MHz
- Button on **PB0** (Arduino pin 8), active-low, internal pull-up
- Serial terminal at **9600 baud**

## Key concepts

- Pin Change Interrupts vs. dedicated external interrupts (INT0/INT1)
- Distinguishing edges in software since PCINT fires on both

## Build & flash

```bash
cd examples/avr/pcint-counter
pymcu build
pymcu flash --port /dev/cu.usbmodemXXXX
```
