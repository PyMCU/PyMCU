# -----------------------------------------------------------------------------
# Whipsnake Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the Whipsnake Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------

from whipsnake.types import uint8, inline
from whipsnake.chips import __CHIP__


# noinspection PyProtectedMember
class I2C:
    """Hardware I2C (TWI) master, zero-cost abstraction (all methods @inline).

    Operates at 100 kHz by default; SDA and SCL pins are defined by the
    target chip. Provides both low-level primitives (start, stop, write,
    read_ack, read_nack) and high-level helpers (ping, write_to, read_from).

    Status code constants (use as dotted-name match patterns)::

        I2C.START, I2C.SLA_ACK, I2C.SLA_NACK, I2C.DATA_ACK, I2C.SLA_R_ACK

    Context manager support: ``with i2c:`` auto-sends START/STOP.
    """

    # TWI status codes - use as dotted-name match patterns so IDEs treat them
    # as value patterns rather than capture patterns.
    START     = 0x08   # START condition transmitted OK
    SLA_ACK   = 0x18   # SLA+W sent, ACK received  (device present)
    SLA_NACK  = 0x20   # SLA+W sent, NACK received (no device)
    DATA_ACK  = 0x28   # Data byte sent, ACK received
    SLA_R_ACK = 0x40   # SLA+R sent, ACK received

    @inline
    def __init__(self):
        """Initialise the I2C peripheral."""
        match __CHIP__.arch:
            case "avr":
                from whipsnake.hal._i2c.avr import i2c_init
                i2c_init()

    @inline
    def ping(self, addr: uint8) -> uint8:
        """Return 1 if a device responds at the given 7-bit address, 0 otherwise."""
        match __CHIP__.arch:
            case "avr":
                from whipsnake.hal._i2c.avr import i2c_ping
                return i2c_ping(addr)
            case _:
                return 0

    @inline
    def start(self) -> uint8:
        """Send a START condition. Returns the TWI status byte."""
        match __CHIP__.arch:
            case "avr":
                from whipsnake.hal._i2c.avr import i2c_start
                return i2c_start()
            case _:
                return 0

    @inline
    def stop(self):
        """Send a STOP condition and release the bus."""
        match __CHIP__.arch:
            case "avr":
                from whipsnake.hal._i2c.avr import i2c_stop
                i2c_stop()

    @inline
    def end(self):
        """Send a STOP condition. Alias for stop()."""
        match __CHIP__.arch:
            case "avr":
                from whipsnake.hal._i2c.avr import i2c_stop
                i2c_stop()

    @inline
    def write(self, data: uint8) -> uint8:
        """Send one byte. Returns the TWI status byte (ACK/NACK)."""
        match __CHIP__.arch:
            case "avr":
                from whipsnake.hal._i2c.avr import i2c_write
                return i2c_write(data)
            case _:
                return 0

    @inline
    def read_ack(self) -> uint8:
        """Receive one byte and send ACK (more bytes to follow)."""
        match __CHIP__.arch:
            case "avr":
                from whipsnake.hal._i2c.avr import i2c_read_ack
                return i2c_read_ack()
            case _:
                return 0

    @inline
    def read_nack(self) -> uint8:
        """Receive one byte and send NACK (last byte in transfer)."""
        match __CHIP__.arch:
            case "avr":
                from whipsnake.hal._i2c.avr import i2c_read_nack
                return i2c_read_nack()
            case _:
                return 0

    @inline
    def write_to(self, addr: uint8, data: uint8) -> uint8:
        # Send START, address byte (write), one data byte, then STOP.
        # addr: 7-bit I2C address; data: byte to send.
        # Returns 1 on success (ACK), 0 if address NACK.
        match __CHIP__.arch:
            case "avr":
                from whipsnake.hal._i2c.avr import i2c_write_to
                return i2c_write_to(addr, data)
            case _:
                return 0

    @inline
    def read_from(self, addr: uint8) -> uint8:
        # Send START, address byte (read), read one byte with NACK, then STOP.
        # addr: 7-bit I2C address. Returns the byte read, or 0 if NACK.
        match __CHIP__.arch:
            case "avr":
                from whipsnake.hal._i2c.avr import i2c_read_from
                return i2c_read_from(addr)
            case _:
                return 0

    # Context manager support: `with i2c:` auto-sends START/STOP
    @inline
    def __enter__(self):
        self.start()

    @inline
    def __exit__(self):
        self.stop()
