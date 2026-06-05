# uart-echo

The simplest UART round-trip: echo every received byte.

## What it does

Reads a byte with `uart.read()` (blocking) and writes it straight back. Prints
an `ECHO` banner on boot. A great second example after [`blink`](../blink) and
the starting point for any serial work.

## Hardware

- Any ATmega328P board @ 16 MHz
- Serial terminal at **9600 8N1** — everything you type echoes back

## Key concepts

- `pymcu.hal.uart.UART` — `read()` / `write()` / `println()`

## Build & flash

```bash
cd examples/avr/uart-echo
pymcu build
pymcu flash --port /dev/cu.usbmodemXXXX
```
