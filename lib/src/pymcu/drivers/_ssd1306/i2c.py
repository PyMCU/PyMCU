# SSD1306 I2C implementation
# Loaded by ssd1306.py via: from pymcu.drivers._ssd1306.i2c import ...
#
# I2C protocol for SSD1306:
#   Command byte: START + (addr<<1)+W + 0x00 (control=command) + cmd + STOP
#   Data byte:    START + (addr<<1)+W + 0x40 (control=data)    + dat + STOP
#
# addr is the 7-bit I2C address (0x3C or 0x3D).
#
# The I2C object is passed as a uint8 (ZCA virtual instance member address).
# All functions are @inline to allow the compiler to fold addr as const.
from pymcu.types import uint8, uint16, inline
from pymcu.hal.i2c import I2C
from pymcu.time import delay_us


@inline
def ssd1306_send_cmd(i2c: uint8, addr: uint8, cmd: uint8):
    # Send one command byte to the SSD1306.
    # Control byte 0x00 = Co=0, D/C#=0 (command stream)
    i2c.start()
    i2c.write((addr << 1) & 0xFE)
    i2c.write(0x00)
    i2c.write(cmd)
    i2c.stop()


@inline
def ssd1306_send_data(i2c: uint8, addr: uint8, dat: uint8):
    # Send one data byte to the SSD1306 GDDRAM.
    # Control byte 0x40 = Co=0, D/C#=1 (data stream)
    i2c.start()
    i2c.write((addr << 1) & 0xFE)
    i2c.write(0x40)
    i2c.write(dat)
    i2c.stop()


@inline
def ssd1306_init_seq(i2c: uint8, addr: uint8):
    # Standard SSD1306 128x64 initialization sequence.
    ssd1306_send_cmd(i2c, addr, 0xAE)
    ssd1306_send_cmd(i2c, addr, 0xD5)
    ssd1306_send_cmd(i2c, addr, 0x80)
    ssd1306_send_cmd(i2c, addr, 0xA8)
    ssd1306_send_cmd(i2c, addr, 0x3F)
    ssd1306_send_cmd(i2c, addr, 0xD3)
    ssd1306_send_cmd(i2c, addr, 0x00)
    ssd1306_send_cmd(i2c, addr, 0x40)
    ssd1306_send_cmd(i2c, addr, 0x8D)
    ssd1306_send_cmd(i2c, addr, 0x14)
    ssd1306_send_cmd(i2c, addr, 0x20)
    ssd1306_send_cmd(i2c, addr, 0x00)
    ssd1306_send_cmd(i2c, addr, 0xA1)
    ssd1306_send_cmd(i2c, addr, 0xC8)
    ssd1306_send_cmd(i2c, addr, 0xDA)
    ssd1306_send_cmd(i2c, addr, 0x12)
    ssd1306_send_cmd(i2c, addr, 0x81)
    ssd1306_send_cmd(i2c, addr, 0xCF)
    ssd1306_send_cmd(i2c, addr, 0xD9)
    ssd1306_send_cmd(i2c, addr, 0xF1)
    ssd1306_send_cmd(i2c, addr, 0xDB)
    ssd1306_send_cmd(i2c, addr, 0x40)
    ssd1306_send_cmd(i2c, addr, 0xA4)
    ssd1306_send_cmd(i2c, addr, 0xA6)
    ssd1306_send_cmd(i2c, addr, 0xAF)


@inline
def ssd1306_set_addr_window(i2c: uint8, addr: uint8):
    # Set column and page address to full display for a complete buffer flush.
    # Column: 0 to 127
    ssd1306_send_cmd(i2c, addr, 0x21)
    ssd1306_send_cmd(i2c, addr, 0)
    ssd1306_send_cmd(i2c, addr, 127)
    # Page: 0 to 7 (8 pages x 8 rows = 64 rows)
    ssd1306_send_cmd(i2c, addr, 0x22)
    ssd1306_send_cmd(i2c, addr, 0)
    ssd1306_send_cmd(i2c, addr, 7)
