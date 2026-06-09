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
from pymcu.types import uint8, const, inline
from pymcu.hal.avr.i2c.avr import i2c_start, i2c_write, i2c_stop


# Standard SSD1306 128x64 init sequence, flash-resident (const -> LPM lookup).
# Driven by a runtime loop in _ssd1306_init so the 25 commands cost one shared
# call + a 25-byte table instead of 25 inlined LDI/RCALL sends.
_SSD1306_INIT: const[uint8[25]] = [0xAE, 0xD5, 0x80, 0xA8, 0x3F, 0xD3, 0x00, 0x40, 0x8D, 0x14, 0x20, 0x00, 0xA1, 0xC8, 0xDA, 0x12, 0x81, 0xCF, 0xD9, 0xF1, 0xDB, 0x40, 0xA4, 0xA6, 0xAF]


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


def _ssd1306_init(addr: uint8):
    # Loop the flash-resident init table: one shared command path for all 25 bytes.
    i: uint8 = 0
    while i < 25:
        _ssd1306_cmd(addr, _SSD1306_INIT[i])
        i = i + 1


@inline
def ssd1306_init_seq(i2c: uint8, addr: uint8):
    # Standard SSD1306 128x64 initialization sequence (flash table + loop).
    _ssd1306_init(addr)


@inline
def ssd1306_set_addr_window(i2c: uint8, addr: uint8):
    # Full-display column (0..127) and page (0..7) window for a complete buffer flush.
    _ssd1306_cmd(addr, 0x21)
    _ssd1306_cmd(addr, 0)
    _ssd1306_cmd(addr, 127)
    _ssd1306_cmd(addr, 0x22)
    _ssd1306_cmd(addr, 0)
    _ssd1306_cmd(addr, 7)
