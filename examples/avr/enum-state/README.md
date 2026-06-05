# enum-state

A minimal compile-time constant-folding demo.

## What it does

Writes a few values over UART that exercise compile-time integer folding and
type truncation: `uint8(300)` folds to `44`, `uint8(256)` folds to `0`, and
`uint16(42)` stays `42`. Useful as a tiny sanity check of the compiler's
constant-folding and width semantics rather than a hardware demo.

## Hardware

- Arduino Uno / any ATmega328P board @ 16 MHz
- Serial terminal at **9600 baud**

## Key concepts

- Compile-time constant folding
- `uint8` / `uint16` width truncation

## Build & flash

```bash
cd examples/avr/enum-state
pymcu build
pymcu flash --port /dev/cu.usbmodemXXXX
```
