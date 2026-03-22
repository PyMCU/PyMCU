# MAX7219 8x8 LED matrix driver (SPI interface)
# Zero-cost abstraction -- follows the DHT11/NeoPixel driver pattern.
#
# Usage:
#   from whisnake.drivers.max7219 import MAX7219
#   from whisnake.hal.spi import SPI
#
#   spi = SPI(cs="PB2")
#   mx = MAX7219(spi)
#   mx.init()
#   mx.clear()
#   mx.set_row(row, data)
#   mx.set_brightness(level)
#
# MAX7219 register addresses:
#   0x00 = no-op         0x09 = decode mode
#   0x0A = intensity     0x0B = scan limit
#   0x0C = shutdown      0x0F = display test
#   0x01-0x08 = digit/row 1-8
#
# Each write: CS low, 2 bytes (addr, data), CS high.
# The SPI object handles CS via __enter__/__exit__ context manager.
from whisnake.chips import __CHIP__
from whisnake.types import uint8, inline


class MAX7219:

    @inline
    def __init__(self, spi: uint8):
        self._spi = spi

    @inline
    def _write_reg(self, reg: uint8, val: uint8):
        # Send one MAX7219 register write: CS low, addr byte, data byte, CS high.
        match __CHIP__.arch:
            case "avr":
                self._spi.select()
                self._spi.write(reg)
                self._spi.write(val)
                self._spi.deselect()

    @inline
    def init(self):
        # Normal operation (not shutdown): 0x0C = 0x01
        self._write_reg(0x0C, 0x01)
        # Decode mode off (raw LED control): 0x09 = 0x00
        self._write_reg(0x09, 0x00)
        # Intensity medium (8 of 15): 0x0A = 0x08
        self._write_reg(0x0A, 0x08)
        # Scan all 8 digits: 0x0B = 0x07
        self._write_reg(0x0B, 0x07)
        # Display test off: 0x0F = 0x00
        self._write_reg(0x0F, 0x00)

    @inline
    def clear(self):
        # Zero all 8 digit/row registers.
        self._write_reg(0x01, 0x00)
        self._write_reg(0x02, 0x00)
        self._write_reg(0x03, 0x00)
        self._write_reg(0x04, 0x00)
        self._write_reg(0x05, 0x00)
        self._write_reg(0x06, 0x00)
        self._write_reg(0x07, 0x00)
        self._write_reg(0x08, 0x00)

    @inline
    def set_row(self, row: uint8, data: uint8):
        # Set one row (0-7) to the given 8-bit LED pattern.
        # MAX7219 digit registers are 0x01 (row 0) through 0x08 (row 7).
        reg: uint8 = row + 1
        self._write_reg(reg, data)

    @inline
    def set_brightness(self, level: uint8):
        # Set intensity: 0 (minimum) to 15 (maximum).
        val: uint8 = level & 0x0F
        self._write_reg(0x0A, val)
