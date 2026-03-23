# -----------------------------------------------------------------------------
# Whipsnake Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the Whipsnake Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------

from whipsnake.types import uint8, inline
from whipsnake.chips import __CHIP__
from whipsnake.hal.gpio import Pin


# noinspection PyProtectedMember
class SoftSPI:
    """Bit-bang SPI master, zero-cost abstraction (all methods @inline).

    Implements Mode 0 (CPOL=0, CPHA=0), MSB-first SPI in software.
    Pin instances are stored directly; pin.high()/low()/value() each compile
    to a single SBI/CBI/SBIS/SBIC instruction with no runtime dispatch.

    Context manager support: ``with spi:`` auto-selects and deselects::

        with SoftSPI(sck_pin, mosi_pin, miso_pin, cs=cs_pin):
            ...
    """

    def __init__(self, sck: Pin, mosi: Pin, miso: Pin, cs: Pin = None):
        """Configure the bit-bang SPI pins.

        sck, mosi, miso, cs: Pin instances configured by the caller.
        cs:                   optional chip-select pin, idle high when set.
        """
        match __CHIP__.arch:
            case "avr":
                # Idle SCK and MOSI low.
                sck.low()
                mosi.low()
                # Store Pin instances; transfer uses pin.high()/low()/value().
                self._sck  = sck
                self._mosi = mosi
                self._miso = miso
                if cs != None:
                    cs.high()  # idle high
                    self._cs_pin = cs
                    self._cs = cs.name
                else:
                    self._cs = ""

    @inline
    def transfer(self, data: uint8) -> uint8:
        """Send one byte MSB-first and simultaneously receive one byte."""
        match __CHIP__.arch:
            case "avr":
                from whipsnake.hal._softspi.avr import softspi_transfer
                return softspi_transfer(self._sck, self._mosi, self._miso, data)
            case _:
                return 0

    @inline
    def write(self, data: uint8):
        """Send one byte; the received byte is discarded."""
        match __CHIP__.arch:
            case "avr":
                from whipsnake.hal._softspi.avr import softspi_transfer
                softspi_transfer(self._sck, self._mosi, self._miso, data)

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
