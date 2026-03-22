# ATmega328P: SSD1306 128x64 OLED over I2C
#
# Demonstrates the SSD1306 driver with a 128x64 OLED display.
# Hardware:
#   SDA -> PC4 (A4), SCL -> PC5 (A5), I2C address 0x3C
#
# UART output (9600 baud):
#   "OLED\n"  -- boot banner
#   "OK\n"    -- init complete
#
from whisnake.types import uint8
from whisnake.hal.uart import UART
from whisnake.hal.i2c import I2C
from pymcu.drivers.ssd1306 import SSD1306


def main():
    uart = UART(9600)
    i2c = I2C()
    oled = SSD1306(i2c, 0x3C)

    uart.println("OLED")

    oled.init()

    uart.println("OK")

    oled.clear()
    oled.pixel(0, 0, 1)
    oled.pixel(127, 63, 1)

    while True:
        pass
