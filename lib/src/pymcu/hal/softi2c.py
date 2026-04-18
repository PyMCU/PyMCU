# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
#
# Software I2C (bit-bang) -- architecture-independent implementation.
#
# Open-drain emulation: SCL and SDA are driven low or released (high via
# pull-up) on every clock phase.  Both lines must have external pull-ups.
#
# Controller-only mode.  Standard-mode 100 kHz compliant when delay_us(5)
# is used as the half-period (half_us=5).  Reduce half_us for higher speeds.
#
# Typical usage:
#
#   scl = Pin("PC5", Pin.OUT)
#   sda = Pin("PC4", Pin.OUT)
#   i2c = SoftI2C(scl, sda)
#   i2c.init()
#   ack = i2c.write_to(0x48, 0xA5)       # single-byte write
#   rx  = i2c.read_from(0x48)            # single-byte read
# -----------------------------------------------------------------------------

from pymcu.types import uint8, inline
from pymcu.hal.gpio import Pin
from pymcu.time import delay_us


# noinspection PyProtectedMember
class SoftI2C:
    """Bit-bang I2C controller, zero-cost abstraction.

    Implements standard-mode I2C (100 kHz) in software using Pin ZCA methods.
    Architecture-independent.

    Usage::

        scl = Pin("PC5", Pin.OUT)
        sda = Pin("PC4", Pin.OUT)
        i2c = SoftI2C(scl, sda)
        i2c.init()
        ack = i2c.write_to(0x48, data)
        rx  = i2c.read_from(0x48)
    """

    def __init__(self, scl: Pin, sda: Pin, half_us: uint8 = 5):
        """Configure the bit-bang I2C pins.

        scl:      Pin instance for the clock line (must have external pull-up).
        sda:      Pin instance for the data line (must have external pull-up).
        half_us:  half-period in microseconds (default 5 -> ~100 kHz).
                  Set to 0 to remove delays (maximum speed; no timing guarantee).
        """
        self._scl     = scl
        self._sda     = sda
        self._half_us = half_us

    @inline
    def init(self):
        """Release both lines high (idle state).  Call before any transfer."""
        self._scl.high()
        self._sda.high()

    @inline
    def _scl_high(self):
        self._scl.high()
        if self._half_us > 0:
            delay_us(self._half_us)

    @inline
    def _scl_low(self):
        self._scl.low()
        if self._half_us > 0:
            delay_us(self._half_us)

    @inline
    def start(self):
        """Send I2C START condition: SDA falls while SCL is high."""
        self._sda.high()
        self._scl_high()
        self._sda.low()
        self._scl_low()

    @inline
    def stop(self):
        """Send I2C STOP condition: SDA rises while SCL is high."""
        self._sda.low()
        self._scl_high()
        self._sda.high()
        if self._half_us > 0:
            delay_us(self._half_us)

    @inline
    def write(self, data: uint8) -> uint8:
        """Transmit one byte MSB-first and return the ACK bit.

        Returns 0 if the slave acknowledged (ACK = SDA low), 1 if NACK.
        """
        tx: uint8 = data
        i: uint8 = 0
        while i < 8:
            # Set SDA to MSB before rising SCL edge.
            if tx & 0x80:
                self._sda.high()
            else:
                self._sda.low()
            self._scl_high()
            self._scl_low()
            tx = tx << 1
            i = i + 1
        # Release SDA and read ACK bit from slave.
        self._sda.high()
        self._scl_high()
        ack: uint8 = self._sda.value()
        self._scl_low()
        return ack   # 0 = ACK, 1 = NACK

    @inline
    def read(self, send_ack: uint8) -> uint8:
        """Receive one byte MSB-first.

        send_ack: 1 to send ACK after the byte (more bytes to follow),
                  0 to send NACK (last byte in transfer).
        Returns the received byte.
        """
        self._sda.high()   # release SDA so slave can drive it
        result: uint8 = 0
        i: uint8 = 0
        while i < 8:
            result = result << 1
            self._scl_high()
            if self._sda.value():
                result = result | 1
            self._scl_low()
            i = i + 1
        # Send ACK or NACK.
        if send_ack:
            self._sda.low()    # ACK
        else:
            self._sda.high()   # NACK
        self._scl_high()
        self._scl_low()
        self._sda.high()       # release SDA
        return result

    @inline
    def write_to(self, addr: uint8, data: uint8) -> uint8:
        """Send START, SLA+W, one data byte, STOP.

        Returns 1 if both address and data were ACK'd, 0 on any NACK.
        """
        self.start()
        sla_w: uint8 = (addr << 1) & 0xFE   # SLA+W: addr<<1 | 0
        addr_ack: uint8 = self.write(sla_w)
        if addr_ack == 0:     # 0 = ACK
            data_ack: uint8 = self.write(data)
            self.stop()
            if data_ack == 0:
                return 1
        else:
            self.stop()
        return 0

    @inline
    def write_bytes(self, addr: uint8, buf, n: uint8) -> uint8:
        """Send START, SLA+W, n bytes from buf[], STOP.

        Returns 1 if address and all data bytes were ACK'd, 0 on any NACK.
        """
        self.start()
        sla_w: uint8 = (addr << 1) & 0xFE
        addr_ack: uint8 = self.write(sla_w)
        if addr_ack == 0:     # 0 = ACK
            i: uint8 = 0
            all_ack: uint8 = 1
            while i < n:
                byte_ack: uint8 = self.write(buf[i])
                if byte_ack != 0:   # NACK
                    all_ack = 0
                    i = n  # break
                i = i + 1
            self.stop()
            return all_ack
        self.stop()
        return 0

    @inline
    def read_from(self, addr: uint8) -> uint8:
        """Send START, SLA+R, receive one byte with NACK, STOP.

        Returns the byte received, or 0 if address not acknowledged.
        """
        self.start()
        sla_r: uint8 = (addr << 1) | 1   # SLA+R: addr<<1 | 1
        addr_ack: uint8 = self.write(sla_r)
        if addr_ack == 0:   # 0 = ACK
            rx_byte: uint8 = self.read(0)   # 0 = NACK (last byte)
            self.stop()
            return rx_byte
        self.stop()
        return 0

    @inline
    def ping(self, addr: uint8) -> uint8:
        """Return 1 if a device acknowledges at the given 7-bit address."""
        self.start()
        sla_w: uint8 = (addr << 1) & 0xFE
        ack: uint8 = self.write(sla_w)
        self.stop()
        if ack == 0:
            return 1
        return 0
