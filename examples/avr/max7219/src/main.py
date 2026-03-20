# ATmega328P: MAX7219 8x8 LED matrix driver via SPI
#
# Demonstrates the MAX7219 driver. Initializes the matrix, clears it,
# sets a test pattern on row 0, and adjusts brightness.
# Hardware:
#   MOSI -> PB3, SCK -> PB5, CS -> PB2
#
# UART output (9600 baud):
#   "MAX7219\n"  -- boot banner
#   "OK\n"       -- init complete
#
from pymcu.types import uint8
from pymcu.hal.uart import UART
from pymcu.hal.spi import SPI
from pymcu.drivers.max7219 import MAX7219
from pymcu.time import delay_ms


def main():
    uart = UART(9600)
    spi = SPI(cs="PB2")
    mx = MAX7219(spi)

    uart.println("MAX7219")

    mx.init()
    mx.clear()

    uart.println("OK")

    # Write a simple checkerboard pattern to all 8 rows
    row: uint8 = 0
    pattern: uint8 = 0xAA
    while row < 8:
        mx.set_row(row, pattern)
        pattern ^= 0xFF
        row += 1

    mx.set_brightness(8)

    delay_ms(500)

    # Scroll: shift each row up
    while True:
        row = 0
        while row < 8:
            mx.set_row(row, row)
            row += 1
        delay_ms(200)
