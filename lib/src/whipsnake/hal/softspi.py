# -----------------------------------------------------------------------------
# Whipsnake Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the Whipsnake Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
#
# Software SPI (bit-bang) -- architecture-independent implementation.
#
# Mode 0 (CPOL=0, CPHA=0), MSB-first.
#
# transfer() uses the shift-left trick: tx and result are shifted left each
# iteration so bit 7 is always the active bit, eliminating per-bit mask
# constants.
#
# half_us: half-period in microseconds; computed from the baudrate (kHz)
# parameter at construction time as 500 // baudrate.  When half_us == 0
# (baudrate > 500 kHz) the guards fold at compile time and both delay_us
# calls are eliminated entirely.
# -----------------------------------------------------------------------------

from whipsnake.types import uint8, uint16, inline
from whipsnake.hal.gpio import Pin
from whipsnake.time import delay_us


# noinspection PyProtectedMember
class SoftSPI:
    """Bit-bang SPI master, zero-cost abstraction (all methods @inline).

    Implements Mode 0 (CPOL=0, CPHA=0), MSB-first SPI in software using
    only Pin ZCA methods -- architecture-independent.

    Pin instances are stored directly; pin.high()/low()/value() each compile
    to a single SBI/CBI/SBIS/SBIC instruction with no runtime dispatch.

    baudrate: target SCK frequency in kHz (default 500 kHz).  The half-period
    delay is computed as ``500 // baudrate`` microseconds.  When baudrate > 500
    the result rounds to 0 and both delay_us calls are eliminated entirely.

    Context manager support: ``with spi:`` auto-selects and deselects::

        with SoftSPI(sck_pin, mosi_pin, miso_pin, cs=cs_pin):
            ...
    """

    def __init__(self, sck: Pin, mosi: Pin, miso: Pin, cs: Pin = None, baudrate: uint16 = 500):
        """Configure the bit-bang SPI pins.

        sck, mosi, miso: Pin instances configured by the caller.
        cs:              optional chip-select pin, idle high when set.
        baudrate:        target SCK frequency in kHz; default 500 kHz.
        """
        # Idle SCK and MOSI low.
        sck.low()
        mosi.low()
        # Store Pin instances; transfer uses pin.high()/low()/value().
        self._sck  = sck
        self._mosi = mosi
        self._miso = miso
        # Half-period in microseconds: 500 us / baudrate_kHz.
        # Folds to 0 when baudrate > 500 kHz; delay_us calls removed by DCE.
        half_us: uint8 = 500 // baudrate
        self._half_us = half_us
        if cs is not None:
            cs.high()  # idle high
            self._cs_pin = cs
            self._cs = cs.name
        else:
            self._cs = ""

    @inline
    def transfer(self, data: uint8) -> uint8:
        """Send one byte MSB-first and simultaneously receive one byte.

        Uses the shift-left trick: tx and result are shifted left each
        iteration so bit 7 is always the active bit.
        """
        tx: uint8 = data
        result: uint8 = 0
        i: uint8 = 0
        while i < 8:
            if tx & 0x80:
                self._mosi.high()
            else:
                self._mosi.low()
            if self._half_us > 0:
                delay_us(self._half_us)
            self._sck.high()
            result = result << 1
            if self._miso.value():
                result = result | 1
            if self._half_us > 0:
                delay_us(self._half_us)
            self._sck.low()
            tx = tx << 1
            i = i + 1
        return result

    @inline
    def write(self, data: uint8):
        """Send one byte; the received byte is discarded."""
        self.transfer(data)

    @inline
    def select(self):
        """Assert the chip-select line low (if cs was configured)."""
        if self._cs != "":
            self._cs_pin.low()

    @inline
    def deselect(self):
        """Deassert the chip-select line high (if cs was configured)."""
        if self._cs != "":
            self._cs_pin.high()

    def __enter__(self):
        """Assert the chip-select line (context manager entry)."""
        self.select()

    def __exit__(self):
        """Deassert the chip-select line (context manager exit)."""
        self.deselect()
