# UART Echo -- CircuitPython style on Arduino Uno
#
# Demonstrates:
#   board module   -- board.TX / board.RX / board.LED resolved at compile time
#   busio module   -- UART serial read/write
#   digitalio      -- led.direction and led.value via property setters
#   print()        -- built-in print routed to UART (like CircuitPython REPL serial)
#
# Adapted from Adafruit CircuitPython Essentials UART Serial example
# Original: Copyright (c) 2018 Kattni Rembor, Adafruit Industries (MIT)
#
# Changes from the original CircuitPython code:
#   - uart.read(32)   -> uart.read()  (single-byte blocking; no bytearrays on AVR)
#   - ''.join([chr(b) for b in data]) -> uart.write(byte)  (no list comprehension)
#   - data is not None -> removed  (blocking read always returns a byte)
#   - Added def main() wrapper required by pymcu entry convention
#
# Wiring:
#   LED:    built-in on D13 (no external wiring needed)
#   Serial: connect USB-to-serial adapter to TX (D1) / RX (D0) at 9600 baud
#
import board
import busio
from digitalio import DigitalInOut, Direction


def main():
    led = DigitalInOut(board.LED)
    led.direction = Direction.OUTPUT

    uart = busio.UART(board.TX, board.RX, baudrate=9600)
    print("READY")

    while True:
        byte = uart.read()
        led.value = 1
        uart.write(byte)
        led.value = 0
