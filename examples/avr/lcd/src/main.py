# ATmega328P: HD44780 LCD 4-bit mode example
#
# Demonstrates the LCD driver with a 16x2 HD44780 display.
# Pins:
#   RS -> PD4, EN -> PD5
#   D4 -> PD6, D5 -> PD7, D6 -> PB0, D7 -> PB1
#
# UART output (9600 baud):
#   "LCD\n"   -- boot banner
#   "OK\n"    -- init complete
#
from whipsnake.types import uint8
from whipsnake.hal.uart import UART
from whipsnake.drivers.lcd import LCD


def main():
    uart = UART(9600)
    lcd = LCD(rs="PD4", en="PD5", d4="PD6", d5="PD7", d6="PB0", d7="PB1")

    uart.println("LCD")

    lcd.init()

    uart.println("OK")

    lcd.clear()
    lcd.home()
    lcd.print_str("Hello World")
    lcd.set_cursor(0, 1)
    lcd.print_str("PyMCU")

    while True:
        pass
