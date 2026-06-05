# uart-str

UART string and character output helpers.

## What it does

Demonstrates `uart.write_str()` (compile-time string expansion),
`uart.println()` (string + newline), and single-character literals
(`'T'` → ASCII 84). After the banner it falls into an echo loop.

## Hardware

- Any ATmega328P board @ 16 MHz
- Serial terminal at **9600 8N1**

## Expected output

```
Hello, PyMCU!
UART string support works!
Test
```
(then echoes input)

## Key concepts

- `write_str()` / `println()` and char literals

## Build & flash

```bash
cd examples/avr/uart-str
pymcu build
pymcu flash --port /dev/cu.usbmodemXXXX
```
