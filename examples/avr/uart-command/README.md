# uart-command

A single-character UART command interpreter.

## What it does

Reads command bytes over UART and dispatches them with `match`/`case`:

| Cmd | Action |
|-----|--------|
| `B` | blink LED 5 times |
| `H` | LED on |
| `L` | LED off |
| `T` | toggle LED |
| `S` | print LED status (`0`/`1`) |
| `?` | print help |
| other | echo back with a `?` prefix |

Command constants are wrapped in a `class CMD` so `match`/`case` sees dotted
names as value patterns.

## Hardware

- Arduino Uno @ 16 MHz
- LED on **PB5** (D13)
- Serial terminal at **9600 baud**

## Key concepts

- `match`/`case` command dispatch with dotted-name value patterns
- Interactive UART control

## Build & flash

```bash
cd examples/avr/uart-command
pymcu build
pymcu flash --port /dev/cu.usbmodemXXXX
```
