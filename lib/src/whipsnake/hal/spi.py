# -----------------------------------------------------------------------------
# Whipsnake Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the Whipsnake Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------

from whipsnake.types import uint8, inline, const, ptr
from whipsnake.chips import __CHIP__


# SPI -- Hardware SPI master, zero-cost abstraction (all methods @inline).
# Default: Mode 0, MSB-first, fosc/4, MOSI=PB3, MISO=PB4, SCK=PB5, SS=PB2.
# Optional cs parameter: custom chip-select pin name (e.g. "PB0").
# When cs is non-empty, the CS port pointer and bit are resolved once at
# construction time and stored as self._cs_port / self._cs_bit so that
# select/deselect are single SBI/CBI instructions -- no re-dispatch per call.
# self._cs is kept as a const[str] so the `if self._cs != ""` guards are
# folded to true/false at compile time (zero SRAM cost).
class SPI:

    @inline
    def __init__(self, cs: const[str] = ""):
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
        match __CHIP__.arch:
            case "avr":
                from whipsnake.hal._spi.avr import spi_transfer
                return spi_transfer(data)
            case _:
                return 0

    @inline
    def write(self, data: uint8):
        match __CHIP__.arch:
            case "avr":
                from whipsnake.hal._spi.avr import spi_transfer
                spi_transfer(data)

    # Context manager support: `with spi:` auto-selects/deselects the device.
    @inline
    def __enter__(self):
        self.select()

    @inline
    def __exit__(self):
        self.deselect()
