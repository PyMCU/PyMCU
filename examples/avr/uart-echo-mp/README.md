# uart-echo-mp

UART echo written in **MicroPython** style, compiled by PyMCU.

## What it does

The same echo behaviour as [`uart-echo`](../uart-echo), but using the
MicroPython-compatible `machine` API (`machine.Pin`, `machine.UART`) from the
`pymcu-micropython` compat layer. `machine.Pin(13, Pin.OUT)` resolves integer
pin 13 to `"PB5"` at compile time via DCE.

## Configuration

`pyproject.toml` selects the compat layer:

```toml
[tool.pymcu]
board  = "arduino_uno"
stdlib = ["micropython"]
```

## Hardware

- Arduino Uno @ 16 MHz
- Built-in LED on **D13**
- USB-to-serial on **TX (D1) / RX (D0)** at **9600 baud**

## Build & flash

```bash
cd examples/avr/uart-echo-mp
pymcu build
pymcu flash --port /dev/cu.usbmodemXXXX
```
