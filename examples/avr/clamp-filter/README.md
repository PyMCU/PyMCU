# clamp-filter

Multi-argument functions and a multi-level call chain on AVR.

## What it does

For each byte received over UART it:
1. Clamps the byte to the printable ASCII range `[32, 126]`.
2. Computes a simple 1st-order prediction `(prev + clamped) / 2`.
3. Echoes back `clamped`, `predicted`, `\n`.

`clamp(val, lo, hi)` is a 3-argument non-inline function (exercising the AVR
calling convention R24/R22/R20 with return in R24) and `predict(prev, curr)`
calls `clamp` internally — a two-level call chain with multiple return paths.

## Hardware

- Arduino Uno @ 16 MHz
- UART TX/RX (PD1/PD0) at **9600 baud**

## Expected output

`CLAMP FILTER` banner, then two transformed bytes + newline per input byte.

## Key concepts

- 3-argument calling convention and early `return` paths
- Nested non-inline function calls
- `>> 1` as divide-by-two

## Build & flash

```bash
cd examples/avr/clamp-filter
pymcu build
pymcu flash --port /dev/cu.usbmodemXXXX
```
