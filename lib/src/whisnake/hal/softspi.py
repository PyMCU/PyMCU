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


# SoftSPI -- Bit-bang SPI master, zero-cost abstraction (all methods @inline).
# Mode 0 (CPOL=0, CPHA=0), MSB-first.
# All pin names are compile-time constants (const[str]).
# At construction, each pin is resolved to a port pointer + bit index and stored
# as self._*_port / self._*_bit (or self._miso_pin_reg / self._miso_bit for MISO).
# transfer() calls softspi_transfer_zca() with these pre-resolved values so
# every byte is transferred with pure SBI/CBI/SBIS/SBIC instructions and no
# string dispatch at runtime.
# self._cs is kept as a const[str] so `if self._cs != ""` guards are DCE'd.
class SoftSPI:

    @inline
    def __init__(self, sck: const[str], mosi: const[str], miso: const[str], cs: const[str] = ""):
        self._cs = cs
        match __CHIP__.arch:
            case "avr":
                from whisnake.hal._softspi.avr import softspi_init
                from whisnake.hal._gpio.atmega328p import select_port, select_pin, select_ddr, select_bit
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
        # Send data byte MSB-first; simultaneously receive one byte.
        # All port/bit values are compile-time constants -> SBI/CBI/SBIS/SBIC.
        match __CHIP__.arch:
            case "avr":
                from whisnake.hal._softspi.avr import softspi_transfer_zca
                return softspi_transfer_zca(self._sck_port, self._sck_bit, self._mosi_port, self._mosi_bit, self._miso_pin_reg, self._miso_bit, data)
            case _:
                return 0

    @inline
    def write(self, data: uint8):
        # Transmit one byte; received byte is discarded.
        match __CHIP__.arch:
            case "avr":
                from whisnake.hal._softspi.avr import softspi_transfer_zca
                softspi_transfer_zca(self._sck_port, self._sck_bit, self._mosi_port, self._mosi_bit, self._miso_pin_reg, self._miso_bit, data)

    @inline
    def select(self):
        # Assert CS low (if configured).
        if self._cs != "":
            self._cs_port[self._cs_bit] = 0

    @inline
    def deselect(self):
        # Deassert CS high (if configured).
        if self._cs != "":
            self._cs_port[self._cs_bit] = 1

    # Context manager: `with spi:` auto-selects/deselects the device.
    @inline
    def __enter__(self):
        self.select()

    @inline
    def __exit__(self):
        self.deselect()
