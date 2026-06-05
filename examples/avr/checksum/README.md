# checksum

A running XOR checksum accumulator over UART.

## What it does

Receives bytes over UART. After every 4 received bytes it emits the XOR of those
4 bytes followed by a newline, then resets the accumulator. Demonstrates an
accumulator pattern with augmented assignment (`acc ^= byte`) and a counter with
conditional reset.

## Hardware

- Arduino Uno @ 16 MHz
- UART TX/RX (PD1/PD0) at **9600 baud**

## Protocol

Send 4 bytes → receive `XOR(b0, b1, b2, b3)` then `\n`.

## Key concepts

- `uart.read()` / `uart.write()` round-trip
- AugAssign XOR accumulator

## Build & flash

```bash
cd examples/avr/checksum
pymcu build
pymcu flash --port /dev/cu.usbmodemXXXX
```
