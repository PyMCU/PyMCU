# lcd

Drive a 16x2 **HD44780** character LCD in 4-bit mode.

## What it does

Initializes an HD44780 display via the stdlib `LCD` driver and prints two lines:
`Hello World` on row 0 and `PyMCU` on row 1. UART reports boot and init status.

## Hardware

- Arduino Uno @ 16 MHz
- HD44780 16x2 LCD wired in 4-bit mode:
  - **RS → PD4**, **EN → PD5**
  - **D4 → PD6**, **D5 → PD7**, **D6 → PB0**, **D7 → PB1**
- Serial terminal at **9600 baud**

## Expected output

```
LCD
OK
```
(and "Hello World" / "PyMCU" on the display)

## Key concepts

- `pymcu.drivers.lcd.LCD` with named pin arguments
- `clear()`, `home()`, `set_cursor()`, `print_str()`

## Build & flash

```bash
cd examples/avr/lcd
pymcu build
pymcu flash --port /dev/cu.usbmodemXXXX
```
