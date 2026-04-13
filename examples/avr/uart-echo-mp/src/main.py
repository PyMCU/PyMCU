# UART Echo -- MicroPython style on Arduino Uno
#
# Demonstrates:
#   machine.UART -- read/write single bytes, println
#   utime        -- sleep_ms() with no hardware timer dependency
#   pymcu.hal    -- native Pin for LED (D13 = PB5 built-in LED)
#
# The MicroPython machine.UART and utime APIs map 1:1 to the native HAL
# with zero runtime overhead via @inline.
#
# Note: machine.Pin is not used here because integer pin-ID DCE is a
# known open issue (int -> string constant propagation across inline
# boundaries). Use pymcu.hal.gpio.Pin with port strings directly.
# See: https://github.com/PyMCU-Org/pymcu/issues (track as mp-pin-int)
#
# Wiring:
#   LED:    built-in on D13 (no external wiring needed)
#   Serial: USB-to-serial adapter to TX (D1) / RX (D0) at 9600 baud
#
# Expected behaviour:
#   Startup: sends "READY\n"
#   Loop: echoes every received byte; LED blinks on each byte

from machine import UART
from utime import sleep_ms
from pymcu.hal.gpio import Pin
from pymcu.types import uint8


def main():
    led  = Pin("PB5", Pin.OUT)
    uart = UART(0, 9600)

    uart.println("READY")

    while True:
        b: uint8 = uart.read()
        led.high()
        uart.write(b)
        led.low()
