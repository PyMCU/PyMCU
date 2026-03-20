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

from pymcu.types import uint8, inline, const
from pymcu.chips import __CHIP__


# SPI - Hardware SPI master, zero-cost abstraction (all methods @inline)
# Default: Mode 0, MSB-first, fosc/4, MOSI=PB3, MISO=PB4, SCK=PB5, SS=PB2
# Optional cs parameter: pin name for chip-select (e.g. "PB2").
# When cs is set, __enter__ drives it low and __exit__ drives it high.
class SPI:

    @inline
    def __init__(self: uint8, cs: const[str] = ""):
        # Store the CS pin name (empty string means use the default SS/PB2 via spi_select)
        self._cs = cs
        match __CHIP__.arch:
            case "avr":
                from pymcu.hal._spi.avr import spi_init
                spi_init()
                if cs != "":
                    # Configure the user-specified CS pin as output, idle high
                    from pymcu.hal.gpio import Pin
                    _cs_pin = Pin(cs, Pin.OUT)
                    _cs_pin.high()

    @inline
    def select(self: uint8):
        match __CHIP__.arch:
            case "avr":
                if self._cs != "":
                    from pymcu.hal.gpio import Pin
                    _cs_pin = Pin(self._cs, Pin.OUT)
                    _cs_pin.low()
                else:
                    from pymcu.hal._spi.avr import spi_select
                    spi_select()

    @inline
    def deselect(self: uint8):
        match __CHIP__.arch:
            case "avr":
                if self._cs != "":
                    from pymcu.hal.gpio import Pin
                    _cs_pin = Pin(self._cs, Pin.OUT)
                    _cs_pin.high()
                else:
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

    # Context manager support: `with spi:` auto-selects/deselects the device
    @inline
    def __enter__(self: uint8):
        self.select()

    @inline
    def __exit__(self: uint8):
        self.deselect()
