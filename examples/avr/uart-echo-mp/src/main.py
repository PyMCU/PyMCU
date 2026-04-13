# UART Echo -- MicroPython style on Arduino Uno
#
# Demonstrates:
#   machine.Pin  -- digital I/O with Arduino Uno integer pin numbers
#   machine.UART -- read/write single bytes, println
#
# machine.Pin(13, Pin.OUT) resolves pin 13 to "PB5" at compile time via
# match/case DCE inside the pymcu-micropython compat layer.
#
# Wiring:
#   LED:    built-in on D13 (no external wiring needed)
#   Serial: USB-to-serial adapter to TX (D1) / RX (D0) at 9600 baud
#
# Expected behaviour:
#   Startup: sends "READY\n"
#   Loop: echoes every received byte; LED pulses HIGH during each byte

from machine import Pin, UART
from pymcu.types import uint8


def main():
    led  = Pin(13, Pin.OUT)
    uart = UART(0, 9600)
    uart.println("READY")
    while True:
        b: uint8 = uart.read()
        led.on()
        uart.write(b)
        led.off()
