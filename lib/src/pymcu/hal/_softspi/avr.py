# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# This file is part of the PyMCU Standard Library (pymcu-stdlib).
# Licensed under the GNU General Public License v3. See LICENSE for details.
# -----------------------------------------------------------------------------
#
# AVR Software SPI (bit-bang) HAL -- ATmega328P
#
# SPI Mode 0 (CPOL=0, CPHA=0), MSB-first, no hardware SPI required.
# All pins are controlled via the generic Pin HAL.
#
# Transfer protocol per bit (SCK idle low):
#   1. Set MOSI to the current bit (MSB first)
#   2. Pulse SCK high
#   3. Sample MISO
#   4. Pulse SCK low
# -----------------------------------------------------------------------------

from pymcu.types import uint8, inline, const
from pymcu.hal.gpio import Pin


@inline
def softspi_init(sck: const[str], mosi: const[str], miso: const[str]):
    # Configure SCK and MOSI as outputs, MISO as input.
    # SCK is idle low (CPOL=0).
    _sck_pin  = Pin(sck,  Pin.OUT)
    _mosi_pin = Pin(mosi, Pin.OUT)
    _miso_pin = Pin(miso, Pin.IN)
    _sck_pin.low()
    _mosi_pin.low()


@inline
def softspi_transfer(sck: const[str], mosi: const[str], miso: const[str], data: uint8) -> uint8:
    # Bit-bang SPI Mode 0, MSB-first.
    # Returns received byte (sampled on SCK rising edge).
    _sck_pin  = Pin(sck,  Pin.OUT)
    _mosi_pin = Pin(mosi, Pin.OUT)
    _miso_pin = Pin(miso, Pin.IN)

    result: uint8 = 0
    bit:    uint8 = 7

    # Unroll 8 iterations via compile-time list comprehension is not available here,
    # so use a counted down variable approach with explicit bit checks.
    # Bit 7 (MSB)
    if data & 0x80:
        _mosi_pin.high()
    else:
        _mosi_pin.low()
    _sck_pin.high()
    if _miso_pin.value():
        result = result | 0x80
    _sck_pin.low()

    # Bit 6
    if data & 0x40:
        _mosi_pin.high()
    else:
        _mosi_pin.low()
    _sck_pin.high()
    if _miso_pin.value():
        result = result | 0x40
    _sck_pin.low()

    # Bit 5
    if data & 0x20:
        _mosi_pin.high()
    else:
        _mosi_pin.low()
    _sck_pin.high()
    if _miso_pin.value():
        result = result | 0x20
    _sck_pin.low()

    # Bit 4
    if data & 0x10:
        _mosi_pin.high()
    else:
        _mosi_pin.low()
    _sck_pin.high()
    if _miso_pin.value():
        result = result | 0x10
    _sck_pin.low()

    # Bit 3
    if data & 0x08:
        _mosi_pin.high()
    else:
        _mosi_pin.low()
    _sck_pin.high()
    if _miso_pin.value():
        result = result | 0x08
    _sck_pin.low()

    # Bit 2
    if data & 0x04:
        _mosi_pin.high()
    else:
        _mosi_pin.low()
    _sck_pin.high()
    if _miso_pin.value():
        result = result | 0x04
    _sck_pin.low()

    # Bit 1
    if data & 0x02:
        _mosi_pin.high()
    else:
        _mosi_pin.low()
    _sck_pin.high()
    if _miso_pin.value():
        result = result | 0x02
    _sck_pin.low()

    # Bit 0 (LSB)
    if data & 0x01:
        _mosi_pin.high()
    else:
        _mosi_pin.low()
    _sck_pin.high()
    if _miso_pin.value():
        result = result | 0x01
    _sck_pin.low()

    return result
