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
class SPI:
    """Hardware SPI master, zero-cost abstraction (all methods @inline).

    Operates in Mode 0 (CPOL=0, CPHA=0), MSB-first, at fosc/4 by default.
    Hardware MOSI/MISO/SCK pins are defined by the target chip.

    Optional ``cs`` parameter: port-pin name string for a custom chip-select
    line. When provided the CS port and bit are resolved once at construction
    so that select() / deselect() each compile to a single instruction.
    ``self._cs`` is kept as a ``const[str]`` so the ``cs != ""`` guards are
    folded at compile time with zero SRAM cost.

    Context manager support: ``with spi:`` auto-selects and deselects::

        with spi:
            spi.write(0xFF)
    """

    @inline
    def __init__(self, cs: const[str] = ""):
        """Initialize the SPI peripheral.

        cs: optional port-pin name for a custom chip-select line.
            When provided it is configured as an output idling high.
        """
        self._cs = cs
        match __CHIP__.arch:
            case "avr":
                from whipsnake.hal._spi.avr import spi_init
                spi_init()
                if cs != "":
                    # Resolve CS pin once; configure as output and idle high.
                    from whipsnake.hal._gpio.atmega328p import select_port, select_ddr, select_bit
                    _cs_ddr = select_ddr(cs)
                    _cs_ddr[select_bit(cs)] = 1
                    self._cs_port = select_port(cs)
                    self._cs_bit  = select_bit(cs)
                    self._cs_port[self._cs_bit] = 1

    @inline
    def select(self):
        """Assert the chip-select line (drive it low)."""
        match __CHIP__.arch:
            case "avr":
                if self._cs != "":
                    # Drive CS low via stored port/bit -- single CBI instruction.
                    self._cs_port[self._cs_bit] = 0
                else:
                    from whipsnake.hal._spi.avr import spi_select
                    spi_select()

    @inline
    def deselect(self):
        """Deassert the chip-select line (drive it high)."""
        match __CHIP__.arch:
            case "avr":
                if self._cs != "":
                    # Drive CS high via stored port/bit -- single SBI instruction.
                    self._cs_port[self._cs_bit] = 1
                else:
                    from whipsnake.hal._spi.avr import spi_deselect
                    spi_deselect()

    @inline
    def transfer(self, data: uint8) -> uint8:
        """Send one byte and simultaneously receive one byte. Returns the received byte."""
        match __CHIP__.arch:
            case "avr":
                from whipsnake.hal._spi.avr import spi_transfer
                return spi_transfer(data)
            case _:
                return 0

    @inline
    def write(self, data: uint8):
        """Send one byte; the received byte is discarded."""
        match __CHIP__.arch:
            case "avr":
                from whipsnake.hal._spi.avr import spi_transfer
                spi_transfer(data)

    @inline
    def __enter__(self):
        """Assert the chip-select line (context manager entry)."""
        self.select()

    @inline
    def __exit__(self):
        """Deassert the chip-select line (context manager exit)."""
        self.deselect()
