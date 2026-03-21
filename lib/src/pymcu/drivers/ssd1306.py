# SSD1306 128x64 monochrome OLED driver (I2C interface)
# Zero-cost abstraction -- follows the DHT11/NeoPixel driver pattern.
#
# Usage:
#   from pymcu.drivers.ssd1306 import SSD1306
#   from pymcu.hal.i2c import I2C
#
#   i2c = I2C()
#   oled = SSD1306(i2c, addr=0x3C)
#   oled.init()
#   oled.clear()
#   oled.pixel(x, y, color)
#   oled.show()
#   oled.print_str(x, y, "Hi!")
#
# Internal: 1024-byte SRAM framebuffer (128*64/8) for pixel-level access.
# show() flushes the buffer to GDDRAM via I2C, one byte at a time.
#
# Architecture dispatch: _ssd1306/i2c.py (I2C command/data helpers)
from pymcu.chips import __CHIP__
from pymcu.types import uint8, uint16, inline


# Module-level framebuffer: 1024 bytes of SRAM (128 columns * 8 pages).
# Shared across all SSD1306 instances (typically only one per board).
_ssd1306_buf: uint8[1024] = bytearray(1024)


class SSD1306:

    @inline
    def __init__(self, i2c: uint8, addr: uint8):
        self._i2c = i2c
        self._addr = addr

    @inline
    def init(self):
        match __CHIP__.arch:
            case "avr":
                from pymcu.drivers._ssd1306.i2c import ssd1306_init_seq
                ssd1306_init_seq(self._i2c, self._addr)

    @inline
    def clear(self):
        # Zero the framebuffer.
        i: uint16 = 0
        while i < 1024:
            _ssd1306_buf[i] = 0
            i = i + 1

    @inline
    def pixel(self, x: uint8, y: uint8, color: uint8):
        # Set or clear a pixel at (x, y).
        # Buffer layout: page = y/8, column = x.
        # Byte index = page*128 + x. Bit = y%8.
        if x >= 128:
            return
        if y >= 64:
            return
        page: uint8 = y >> 3
        bit: uint8 = y & 0x07
        idx: uint16 = page
        idx = (idx << 7) + x
        if color:
            _ssd1306_buf[idx] = _ssd1306_buf[idx] | (1 << bit)
        else:
            _ssd1306_buf[idx] = _ssd1306_buf[idx] & ~(1 << bit)

    @inline
    def show(self):
        # Flush the entire 1024-byte framebuffer to GDDRAM via I2C.
        match __CHIP__.arch:
            case "avr":
                from pymcu.drivers._ssd1306.i2c import ssd1306_set_addr_window, ssd1306_send_data
                ssd1306_set_addr_window(self._i2c, self._addr)
                i: uint16 = 0
                while i < 1024:
                    ssd1306_send_data(self._i2c, self._addr, _ssd1306_buf[i])
                    i = i + 1

    @inline
    def print_str(self, x: uint8, y: uint8, s: str):
        # Write ASCII text at pixel position (x, y) to the framebuffer.
        # Writes the raw character codes into the buffer at the given page row.
        # Page row = y / 8. Each character occupies one byte in the page row.
        # For proper 5x7 font rendering, use write_char() after setting cursor.
        # for-in over const[str] is compile-time unrolled by the IR generator.
        page: uint8 = y >> 3
        col: uint8 = x
        for c in s:
            if col < 128:
                idx: uint16 = page
                idx = (idx << 7) + col
                _ssd1306_buf[idx] = c
            col = col + 1
