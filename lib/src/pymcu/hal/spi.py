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

from pymcu.types import uint8, inline
from pymcu.chips import __CHIP__


# SPI - Hardware SPI master, zero-cost abstraction (all methods @inline)
# Default: Mode 0, MSB-first, fosc/4, MOSI=PB3, MISO=PB4, SCK=PB5, SS=PB2
class SPI:

    @inline
    def __init__(self: uint8):
        match __CHIP__.arch:
            case "avr":
                from pymcu.hal._spi.avr import spi_init
                spi_init()

    @inline
    def select(self: uint8):
        match __CHIP__.arch:
            case "avr":
                from pymcu.hal._spi.avr import spi_select
                spi_select()

    @inline
    def deselect(self: uint8):
        match __CHIP__.arch:
            case "avr":
                from pymcu.hal._spi.avr import spi_deselect
                spi_deselect()

    @inline
    def transfer(self: uint8, data: uint8) -> uint8:
        match __CHIP__.arch:
            case "avr":
                from pymcu.hal._spi.avr import spi_transfer
                return spi_transfer(data)
            case _:
                return 0

    @inline
    def write(self: uint8, data: uint8):
        match __CHIP__.arch:
            case "avr":
                from pymcu.hal._spi.avr import spi_transfer
                spi_transfer(data)
