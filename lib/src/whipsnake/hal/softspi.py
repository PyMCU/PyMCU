# -----------------------------------------------------------------------------
# Whipsnake Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the Whipsnake Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------

from whipsnake.types import uint8, inline, const, ptr
from whipsnake.chips import __CHIP__


# noinspection PyProtectedMember
class SoftSPI:
    """Bit-bang SPI master, zero-cost abstraction (all methods @inline).

    Implements Mode 0 (CPOL=0, CPHA=0), MSB-first SPI in software using
    four compile-time constant pin names (SCK, MOSI, MISO, optional CS).

    At construction each pin is resolved to a port pointer and bit index.
    All subsequent operations use these stored values directly, emitting
    only register writes with no string dispatch at runtime.

    ``self._cs`` is a ``const[str]`` so ``cs != ""`` guards are folded at
    compile time, adding zero overhead when CS is not used.

    Context manager support: ``with spi:`` auto-selects and deselects::

        with SoftSPI("PB5", "PB3", "PB4", cs="PB2"):
            ...
    """

    def __init__(self, sck: const[str], mosi: const[str], miso: const[str], cs: const[str] = ""):
        """Configure the bit-bang SPI pins and resolve port/bit pointers.

        sck, mosi, miso: compile-time pin name strings.
        cs:              optional chip-select pin name, idle high when set.
        """
        self._cs = cs
        match __CHIP__.arch:
            case "avr":
                from whipsnake.hal._softspi.avr import softspi_init
                from whipsnake.hal._gpio.atmega328p import select_port, select_pin, select_ddr, select_bit
                # Configure pin directions; SCK and MOSI idle low.
                softspi_init(sck, mosi, miso)
                # Resolve SCK output port/bit.
                self._sck_port = select_port(sck)
                self._sck_bit  = select_bit(sck)
                # Resolve MOSI output port/bit.
                self._mosi_port = select_port(mosi)
                self._mosi_bit  = select_bit(mosi)
                # Resolve MISO input PIN register/bit.
                self._miso_pin_reg = select_pin(miso)
                self._miso_bit     = select_bit(miso)
                if cs != "":
                    # Resolve CS pin; configure as output and idle high.
                    _cs_ddr = select_ddr(cs)
                    _cs_ddr[select_bit(cs)] = 1
                    self._cs_port = select_port(cs)
                    self._cs_bit  = select_bit(cs)
                    self._cs_port[self._cs_bit] = 1

    @inline
    def transfer(self, data: uint8) -> uint8:
        """Send one byte MSB-first and simultaneously receive one byte.

        All port/bit values are compile-time constants, so the loop body
        compiles to direct register writes with no runtime dispatch.
        """
        # Send data byte MSB-first; simultaneously receive one byte.
        match __CHIP__.arch:
            case "avr":
                from whipsnake.hal._softspi.avr import softspi_transfer_zca
                return softspi_transfer_zca(self._sck_port, self._sck_bit, self._mosi_port, self._mosi_bit, self._miso_pin_reg, self._miso_bit, data)
            case _:
                return 0

    @inline
    def write(self, data: uint8):
        """Send one byte; the received byte is discarded."""
        # Transmit one byte; received byte is discarded.
        match __CHIP__.arch:
            case "avr":
                from whipsnake.hal._softspi.avr import softspi_transfer_zca
                softspi_transfer_zca(self._sck_port, self._sck_bit, self._mosi_port, self._mosi_bit, self._miso_pin_reg, self._miso_bit, data)

    @inline
    def select(self):
        """Assert the chip-select line low (if cs was configured)."""
        if self._cs != "":
            self._cs_port[self._cs_bit] = 0

    @inline
    def deselect(self):
        """Deassert the chip-select line high (if cs was configured)."""
        if self._cs != "":
            self._cs_port[self._cs_bit] = 1

    def __enter__(self):
        """Assert the chip-select line (context manager entry)."""
        self.select()

    def __exit__(self):
        """Deassert the chip-select line (context manager exit)."""
        self.deselect()
