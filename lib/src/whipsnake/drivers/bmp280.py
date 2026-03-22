# BMP280 barometric pressure and temperature sensor driver (I2C)
# Zero-cost abstraction -- follows the DHT11/NeoPixel driver pattern.
#
# Usage:
#   from whipsnake.drivers.bmp280 import BMP280
#   from whipsnake.hal.i2c import I2C
#
#   i2c = I2C()
#   bmp = BMP280(i2c, addr=0x76)
#   bmp.init()
#   temp_raw: uint16 = bmp.read_temp_raw()
#   press_raw: uint16 = bmp.read_press_raw()
#
# Returns raw ADC values (16-bit MSB+LSB, drops XLSB).
# Full compensation requires fixed-point math not yet available.
# I2C address: 0x76 (SDO=GND) or 0x77 (SDO=VCC).
from whipsnake.chips import __CHIP__
from whipsnake.types import uint8, uint16, inline


class BMP280:

    @inline
    def __init__(self, i2c: uint8, addr: uint8):
        self._i2c = i2c
        self._addr = addr

    @inline
    def init(self):
        match __CHIP__.arch:
            case "avr":
                from whipsnake.drivers._bmp280.i2c import bmp280_init
                bmp280_init(self._i2c, self._addr)

    @inline
    def read_temp_raw(self) -> uint16:
        match __CHIP__.arch:
            case "avr":
                from whipsnake.drivers._bmp280.i2c import bmp280_read_temp_raw
                return bmp280_read_temp_raw(self._i2c, self._addr)
            case _:
                return 0

    @inline
    def read_press_raw(self) -> uint16:
        match __CHIP__.arch:
            case "avr":
                from whipsnake.drivers._bmp280.i2c import bmp280_read_press_raw
                return bmp280_read_press_raw(self._i2c, self._addr)
            case _:
                return 0
