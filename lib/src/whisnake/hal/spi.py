# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# This file is part of the PyMCU Standard Library (pymcu-stdlib).
#
# pymcu-stdlib is free software: you can redistribute it and/or modify
# it under the terms of the GNU General Public License as published by
# the Free Software Foundation, either version 3 of the License, or
# (at your option) any later version.
#
# pymcu-stdlib is distributed in the hope that it will be useful,
# but WITHOUT ANY WARRANTY; without even the implied warranty of
# MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
# GNU General Public License for more details.
#
# You should have received a copy of the GNU General Public License
# along with pymcu-stdlib.  If not, see <https://www.gnu.org/licenses/>.
#
# -----------------------------------------------------------------------------
# NOTICE: STRICT COPYLEFT & STATIC LINKING - see uart.py for full notice.
# -----------------------------------------------------------------------------

from whisnake.types import uint8, inline, const, ptr
from whisnake.chips import __CHIP__


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
                from whisnake.hal._spi.avr import spi_init
                spi_init()
                if cs != "":
                    # Resolve CS pin once; configure as output and idle high.
                    from whisnake.hal._gpio.atmega328p import select_port, select_ddr, select_bit
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
                    from whisnake.hal._spi.avr import spi_select
                    spi_select()

    @inline
    def deselect(self):
        match __CHIP__.arch:
            case "avr":
                if self._cs != "":
                    # Drive CS high via stored port/bit -- single SBI instruction.
                    self._cs_port[self._cs_bit] = 1
                else:
                    from whisnake.hal._spi.avr import spi_deselect
                    spi_deselect()

    @inline
    def transfer(self, data: uint8) -> uint8:
        match __CHIP__.arch:
            case "avr":
                from whisnake.hal._spi.avr import spi_transfer
                return spi_transfer(data)
            case _:
                return 0

    @inline
    def write(self, data: uint8):
        match __CHIP__.arch:
            case "avr":
                from whisnake.hal._spi.avr import spi_transfer
                spi_transfer(data)

    # Context manager support: `with spi:` auto-selects/deselects the device.
    @inline
    def __enter__(self):
        self.select()

    @inline
    def __exit__(self):
        self.deselect()
