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


# SoftSPI - Bit-bang SPI master, zero-cost abstraction (all methods @inline)
# Mode 0 (CPOL=0, CPHA=0), MSB-first.
# All four pins are specified by name at construction time (compile-time constants).
# Optional cs pin: when set, __enter__ drives it low and __exit__ drives it high.
class SoftSPI:

    @inline
    def __init__(self: uint8, sck: const[str], mosi: const[str], miso: const[str], cs: const[str] = ""):
        # Store pin names as compile-time constants; configure directions.
        self._sck  = sck
        self._mosi = mosi
        self._miso = miso
        self._cs   = cs
        match __CHIP__.arch:
            case "avr":
                from pymcu.hal._softspi.avr import softspi_init
                softspi_init(sck, mosi, miso)
                if cs != "":
                    from pymcu.hal.gpio import Pin
                    _cs_pin = Pin(cs, Pin.OUT)
                    _cs_pin.high()

    @inline
    def transfer(self: uint8, data: uint8) -> uint8:
        # Send data byte and simultaneously receive a byte (SPI full-duplex).
        # Returns the received byte sampled on each SCK rising edge.
        match __CHIP__.arch:
            case "avr":
                from pymcu.hal._softspi.avr import softspi_transfer
                return softspi_transfer(self._sck, self._mosi, self._miso, data)
            case _:
                return 0

    @inline
    def write(self: uint8, data: uint8):
        # Transmit one byte; received byte is discarded.
        match __CHIP__.arch:
            case "avr":
                from pymcu.hal._softspi.avr import softspi_transfer
                softspi_transfer(self._sck, self._mosi, self._miso, data)

    @inline
    def select(self: uint8):
        # Assert the CS pin low (if configured).
        if self._cs != "":
            from pymcu.hal.gpio import Pin
            _cs_pin = Pin(self._cs, Pin.OUT)
            _cs_pin.low()

    @inline
    def deselect(self: uint8):
        # Deassert the CS pin high (if configured).
        if self._cs != "":
            from pymcu.hal.gpio import Pin
            _cs_pin = Pin(self._cs, Pin.OUT)
            _cs_pin.high()

    # Context manager: `with spi:` auto-selects/deselects the device
    @inline
    def __enter__(self: uint8):
        self.select()

    @inline
    def __exit__(self: uint8):
        self.deselect()
