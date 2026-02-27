# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# This file is part of the PyMCU Standard Library (pymcu-stdlib).
# Licensed under the GNU General Public License v3. See LICENSE for details.
# -----------------------------------------------------------------------------
# NOTICE: STRICT COPYLEFT & STATIC LINKING - see uart.py for full notice.
# -----------------------------------------------------------------------------

from pymcu.types import uint8, inline
from pymcu.chips import __CHIP__


# I2C - Hardware TWI master, zero-cost abstraction (all methods @inline)
# Default: 100 kHz, SDA=PC4, SCL=PC5 (Arduino Uno A4/A5)
# Status constants for match patterns: I2C.START, I2C.SLA_ACK, I2C.SLA_NACK
# High-level: i2c.ping(addr) returns 1 if device present, 0 if not
class I2C:

    # TWI status codes - use as dotted-name match patterns so IDEs treat them
    # as value patterns rather than capture patterns.
    START     = 0x08   # START condition transmitted OK
    SLA_ACK   = 0x18   # SLA+W sent, ACK received  (device present)
    SLA_NACK  = 0x20   # SLA+W sent, NACK received (no device)
    DATA_ACK  = 0x28   # Data byte sent, ACK received
    SLA_R_ACK = 0x40   # SLA+R sent, ACK received

    @inline
    def __init__(self: uint8):
        match __CHIP__.arch:
            case "avr":
                from pymcu.hal._i2c.avr import i2c_init
                i2c_init()

    @inline
    def ping(self: uint8, addr: uint8) -> uint8:
        match __CHIP__.arch:
            case "avr":
                from pymcu.hal._i2c.avr import i2c_ping
                return i2c_ping(addr)
            case _:
                return 0

    @inline
    def start(self: uint8) -> uint8:
        match __CHIP__.arch:
            case "avr":
                from pymcu.hal._i2c.avr import i2c_start
                return i2c_start()
            case _:
                return 0

    @inline
    def stop(self: uint8):
        match __CHIP__.arch:
            case "avr":
                from pymcu.hal._i2c.avr import i2c_stop
                i2c_stop()

    @inline
    def end(self: uint8):
        match __CHIP__.arch:
            case "avr":
                from pymcu.hal._i2c.avr import i2c_stop
                i2c_stop()

    @inline
    def write(self: uint8, data: uint8) -> uint8:
        match __CHIP__.arch:
            case "avr":
                from pymcu.hal._i2c.avr import i2c_write
                return i2c_write(data)
            case _:
                return 0

    @inline
    def read_ack(self: uint8) -> uint8:
        match __CHIP__.arch:
            case "avr":
                from pymcu.hal._i2c.avr import i2c_read_ack
                return i2c_read_ack()
            case _:
                return 0

    @inline
    def read_nack(self: uint8) -> uint8:
        match __CHIP__.arch:
            case "avr":
                from pymcu.hal._i2c.avr import i2c_read_nack
                return i2c_read_nack()
            case _:
                return 0
