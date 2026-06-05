# button-debounce

Software-debounced button press counter with UART telemetry.

## What it does

Reads a button on **PD2**, detects falling edges (press), and on each press
toggles the LED, increments a 16-bit counter, and sends the count over UART as
two big-endian bytes. When the counter reaches 1000 it rolls back to 0 and sends
the character `R`. A 10 ms loop delay provides the debounce window.

## Hardware

- Arduino Uno @ 16 MHz
- Button on **PD2** (active-low, internal pull-up)
- LED on **PB5** (built-in)
- Serial terminal at **9600 baud**

## Key concepts

- Edge detection with a `prev`/`cur` state pair
- 16-bit `uint16` arithmetic and `== 1000` comparison
- Big-endian byte output over UART

## Build & flash

```bash
cd examples/avr/button-debounce
pymcu build
pymcu flash --port /dev/cu.usbmodemXXXX
```
