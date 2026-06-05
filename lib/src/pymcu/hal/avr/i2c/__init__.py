# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
#
# AVR I2C (TWI) facade -- pymcu.hal.avr.i2c
#
# Single implementation covers all AVR chips with hardware TWI.
# -----------------------------------------------------------------------------
from pymcu.types import uint8, inline, Callable
from pymcu.hal.avr.i2c.avr import (
    i2c_init, i2c_start, i2c_stop, i2c_write, i2c_read_ack, i2c_read_nack,
    i2c_ping, i2c_write_to, i2c_write_bytes, i2c_read_from,
    i2c_writeto_mem, i2c_readfrom_mem,
    i2c_peripheral_init, i2c_peripheral_ready, i2c_peripheral_status,
    i2c_peripheral_acknowledge, i2c_peripheral_nack,
    i2c_peripheral_read, i2c_peripheral_write, i2c_irq_setup,
)


class I2C:
    """Hardware I2C (TWI) controller or peripheral, zero-cost abstraction.

    i2c = I2C()        # controller mode (100 kHz)
    i2c = I2C(0x42)    # peripheral mode, address 0x42
    """

    START     = 0x08
    SLA_ACK   = 0x18
    SLA_NACK  = 0x20
    DATA_ACK  = 0x28
    SLA_R_ACK = 0x40

    ADDR_WRITE    = 0x60
    DATA_RECEIVED = 0x80
    LAST_RECEIVED = 0x88
    STOP_RECEIVED = 0xA0
    ADDR_READ     = 0xA8
    DATA_SENT     = 0xB8
    LAST_SENT     = 0xC0

    def __init__(self, addr: uint8 = 0, general_call: uint8 = 0):
        if addr == 0:
            i2c_init()
            self._mode = "c"
        else:
            i2c_peripheral_init(addr, general_call)
            self._mode = "p"

    @inline
    def ping(self, addr: uint8) -> uint8:
        if self._mode == "c":
            return i2c_ping(addr)
        return 0

    @inline
    def start(self) -> uint8:
        if self._mode == "c":
            return i2c_start()
        return 0

    @inline
    def stop(self):
        if self._mode == "c":
            i2c_stop()

    @inline
    def end(self):
        self.stop()

    @inline
    def write(self, data: uint8) -> uint8:
        if self._mode == "c":
            return i2c_write(data)
        else:
            i2c_peripheral_write(data)
        return 0

    @inline
    def read_ack(self) -> uint8:
        if self._mode == "c":
            return i2c_read_ack()
        return 0

    @inline
    def read_nack(self) -> uint8:
        if self._mode == "c":
            return i2c_read_nack()
        return 0

    @inline
    def write_to(self, addr: uint8, data: uint8) -> uint8:
        if self._mode == "c":
            return i2c_write_to(addr, data)
        return 0

    @inline
    def write_bytes(self, addr: uint8, buf, n: uint8) -> uint8:
        if self._mode == "c":
            return i2c_write_bytes(addr, buf, n)
        return 0

    @inline
    def read_from(self, addr: uint8) -> uint8:
        if self._mode == "c":
            return i2c_read_from(addr)
        return 0

    @inline
    def writeto_mem(self, addr: uint8, reg: uint8, data: uint8) -> uint8:
        if self._mode == "c":
            return i2c_writeto_mem(addr, reg, data)
        return 0

    @inline
    def readfrom_mem(self, addr: uint8, reg: uint8, buf, n: uint8) -> uint8:
        if self._mode == "c":
            return i2c_readfrom_mem(addr, reg, buf, n)
        return 0

    @inline
    def ready(self) -> uint8:
        if self._mode == "p":
            return i2c_peripheral_ready()
        return 0

    @inline
    def status(self) -> uint8:
        if self._mode == "p":
            return i2c_peripheral_status()
        return 0

    @inline
    def acknowledge(self):
        if self._mode == "p":
            i2c_peripheral_acknowledge()

    @inline
    def nack(self):
        if self._mode == "p":
            i2c_peripheral_nack()

    @inline
    def read(self) -> uint8:
        if self._mode == "p":
            return i2c_peripheral_read()
        return 0

    @inline
    def irq(self, handler: Callable):
        i2c_irq_setup(handler)

    def __enter__(self):
        self.start()

    def __exit__(self):
        self.stop()
