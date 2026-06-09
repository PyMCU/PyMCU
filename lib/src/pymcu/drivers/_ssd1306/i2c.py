# SSD1306 I2C implementation (AVR)
# Loaded by ssd1306.py via: from pymcu.drivers._ssd1306.i2c import ...
#
# I2C protocol for SSD1306:
#   Command byte: START + (addr<<1)+W + 0x00 (control=command) + cmd + STOP
#   Data byte:    START + (addr<<1)+W + 0x40 (control=data)    + dat + STOP
#
# addr is the 7-bit I2C address (0x3C or 0x3D).
#
# The per-byte transaction is a REAL subroutine (_ssd1306_cmd / _ssd1306_data) that
# talks to the TWI registers directly via primitives. The public @inline wrappers keep
# the (i2c, addr, ...) signature for the ZCA call sites, but route to those subroutines
# so the ~31 init/window command sends share ONE copy instead of force-inlining 31 full
# transactions. (A function that receives the ZCA I2C instance MUST be inlined -- the
# instance has no runtime representation -- so the repetitive work has to live in a
# helper that takes only primitives.)
from pymcu.types import uint8, inline
from pymcu.hal.avr.i2c.avr import i2c_start, i2c_write, i2c_stop


def _ssd1306_cmd(addr: uint8, cmd: uint8):
    # One command byte. Control byte 0x00 = Co=0, D/C#=0 (command stream).
    i2c_start()
    i2c_write((addr << 1) & 0xFE)
    i2c_write(0x00)
    i2c_write(cmd)
    i2c_stop()


def _ssd1306_data(addr: uint8, dat: uint8):
    # One data byte. Control byte 0x40 = Co=0, D/C#=1 (data stream).
    i2c_start()
    i2c_write((addr << 1) & 0xFE)
    i2c_write(0x40)
    i2c_write(dat)
    i2c_stop()


@inline
def ssd1306_send_cmd(i2c: uint8, addr: uint8, cmd: uint8):
    _ssd1306_cmd(addr, cmd)


@inline
def ssd1306_send_data(i2c: uint8, addr: uint8, dat: uint8):
    _ssd1306_data(addr, dat)


@inline
def ssd1306_init_seq(i2c: uint8, addr: uint8):
    # Standard SSD1306 128x64 initialization sequence.
    _ssd1306_cmd(addr, 0xAE)
    _ssd1306_cmd(addr, 0xD5)
    _ssd1306_cmd(addr, 0x80)
    _ssd1306_cmd(addr, 0xA8)
    _ssd1306_cmd(addr, 0x3F)
    _ssd1306_cmd(addr, 0xD3)
    _ssd1306_cmd(addr, 0x00)
    _ssd1306_cmd(addr, 0x40)
    _ssd1306_cmd(addr, 0x8D)
    _ssd1306_cmd(addr, 0x14)
    _ssd1306_cmd(addr, 0x20)
    _ssd1306_cmd(addr, 0x00)
    _ssd1306_cmd(addr, 0xA1)
    _ssd1306_cmd(addr, 0xC8)
    _ssd1306_cmd(addr, 0xDA)
    _ssd1306_cmd(addr, 0x12)
    _ssd1306_cmd(addr, 0x81)
    _ssd1306_cmd(addr, 0xCF)
    _ssd1306_cmd(addr, 0xD9)
    _ssd1306_cmd(addr, 0xF1)
    _ssd1306_cmd(addr, 0xDB)
    _ssd1306_cmd(addr, 0x40)
    _ssd1306_cmd(addr, 0xA4)
    _ssd1306_cmd(addr, 0xA6)
    _ssd1306_cmd(addr, 0xAF)


@inline
def ssd1306_set_addr_window(i2c: uint8, addr: uint8):
    # Full-display column (0..127) and page (0..7) window for a complete buffer flush.
    _ssd1306_cmd(addr, 0x21)
    _ssd1306_cmd(addr, 0)
    _ssd1306_cmd(addr, 127)
    _ssd1306_cmd(addr, 0x22)
    _ssd1306_cmd(addr, 0)
    _ssd1306_cmd(addr, 7)
