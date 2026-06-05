# uart-echo-cp

UART echo written in **CircuitPython** style, compiled by PyMCU.

## What it does

The same echo behaviour as [`uart-echo`](../uart-echo), but using the
CircuitPython-compatible API (`board`, `busio`, `digitalio`) provided by the
`pymcu-circuitpython` compat layer. `board.TX` / `board.RX` / `board.LED` resolve
to concrete pins at compile time; `led.value` / `led.direction` are property
setters. Adapted from the Adafruit CircuitPython Essentials UART example.

This shows PyMCU can compile familiar CircuitPython code to native AVR firmware.

## Configuration

`pyproject.toml` selects the compat layer:

```toml
[tool.pymcu]
board  = "arduino_uno"
stdlib = ["circuitpython"]
```

## Hardware

- Arduino Uno @ 16 MHz
- Built-in LED on **D13**
- USB-to-serial on **TX (D1) / RX (D0)** at **9600 baud**

## Build & flash

```bash
cd examples/avr/uart-echo-cp
pymcu build
pymcu flash --port /dev/cu.usbmodemXXXX
```
