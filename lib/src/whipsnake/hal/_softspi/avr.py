# -----------------------------------------------------------------------------
# Whipsnake Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the Whipsnake Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
#
# AVR Software SPI (bit-bang) HAL
#
# SPI Mode 0 (CPOL=0, CPHA=0), MSB-first.
# softspi_transfer() takes Pin ZCA instances stored by SoftSPI.
# pin.high()/low()/value() each compile to a single SBI/CBI/SBIS/SBIC
# instruction -- identical to the pre-resolved ptr[uint8]+bit approach.
# -----------------------------------------------------------------------------

from whipsnake.types import uint8, inline
from whipsnake.hal.gpio import Pin


@inline
def softspi_transfer(sck: Pin, mosi: Pin, miso: Pin, data: uint8) -> uint8:
    # Bit-bang SPI Mode 0, MSB-first.
    # Each bit: drive MOSI -> strobe SCK high -> sample MISO -> SCK low.
    result: uint8 = 0

    # Bit 7 (MSB)
    if data & 0x80:
        mosi.high()
    else:
        mosi.low()
    sck.high()
    if miso.value():
        result = result | 0x80
    sck.low()

    # Bit 6
    if data & 0x40:
        mosi.high()
    else:
        mosi.low()
    sck.high()
    if miso.value():
        result = result | 0x40
    sck.low()

    # Bit 5
    if data & 0x20:
        mosi.high()
    else:
        mosi.low()
    sck.high()
    if miso.value():
        result = result | 0x20
    sck.low()

    # Bit 4
    if data & 0x10:
        mosi.high()
    else:
        mosi.low()
    sck.high()
    if miso.value():
        result = result | 0x10
    sck.low()

    # Bit 3
    if data & 0x08:
        mosi.high()
    else:
        mosi.low()
    sck.high()
    if miso.value():
        result = result | 0x08
    sck.low()

    # Bit 2
    if data & 0x04:
        mosi.high()
    else:
        mosi.low()
    sck.high()
    if miso.value():
        result = result | 0x04
    sck.low()

    # Bit 1
    if data & 0x02:
        mosi.high()
    else:
        mosi.low()
    sck.high()
    if miso.value():
        result = result | 0x02
    sck.low()

    # Bit 0 (LSB)
    if data & 0x01:
        mosi.high()
    else:
        mosi.low()
    sck.high()
    if miso.value():
        result = result | 0x01
    sck.low()

    return result
