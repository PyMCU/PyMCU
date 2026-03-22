# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# This file is part of the PyMCU Standard Library (pymcu-stdlib).
# Licensed under the GNU General Public License v3. See LICENSE for details.
# -----------------------------------------------------------------------------
#
# AVR Software SPI (bit-bang) HAL
#
# SPI Mode 0 (CPOL=0, CPHA=0), MSB-first.
# softspi_init() configures DDR once at construction time.
# softspi_transfer_zca() takes pre-resolved port/bit values stored by the
# SoftSPI ZCA, avoiding DDR re-initialisation and string dispatch on every byte.
# -----------------------------------------------------------------------------

from whipsnake.types import uint8, inline, const, ptr
from whipsnake.hal.gpio import Pin


@inline
def softspi_init(sck: const[str], mosi: const[str], miso: const[str]):
    # Configure SCK and MOSI as outputs, MISO as input. SCK idles low.
    _sck_pin  = Pin(sck,  Pin.OUT)
    _mosi_pin = Pin(mosi, Pin.OUT)
    _miso_pin = Pin(miso, Pin.IN)
    _sck_pin.low()
    _mosi_pin.low()


@inline
def softspi_transfer_zca(sck_port: ptr[uint8], sck_bit: uint8, mosi_port: ptr[uint8], mosi_bit: uint8, miso_pin_reg: ptr[uint8], miso_bit: uint8, data: uint8) -> uint8:
    # Bit-bang SPI Mode 0, MSB-first, using pre-resolved port/bit from the ZCA.
    # Each bit: set MOSI -> SCK high -> sample MISO -> SCK low.
    # All port/bit values are compile-time constants -> SBI/CBI/SBIS/SBIC.
    result: uint8 = 0

    # Bit 7 (MSB)
    if data & 0x80:
        mosi_port[mosi_bit] = 1
    else:
        mosi_port[mosi_bit] = 0
    sck_port[sck_bit] = 1
    if miso_pin_reg[miso_bit]:
        result = result | 0x80
    sck_port[sck_bit] = 0

    # Bit 6
    if data & 0x40:
        mosi_port[mosi_bit] = 1
    else:
        mosi_port[mosi_bit] = 0
    sck_port[sck_bit] = 1
    if miso_pin_reg[miso_bit]:
        result = result | 0x40
    sck_port[sck_bit] = 0

    # Bit 5
    if data & 0x20:
        mosi_port[mosi_bit] = 1
    else:
        mosi_port[mosi_bit] = 0
    sck_port[sck_bit] = 1
    if miso_pin_reg[miso_bit]:
        result = result | 0x20
    sck_port[sck_bit] = 0

    # Bit 4
    if data & 0x10:
        mosi_port[mosi_bit] = 1
    else:
        mosi_port[mosi_bit] = 0
    sck_port[sck_bit] = 1
    if miso_pin_reg[miso_bit]:
        result = result | 0x10
    sck_port[sck_bit] = 0

    # Bit 3
    if data & 0x08:
        mosi_port[mosi_bit] = 1
    else:
        mosi_port[mosi_bit] = 0
    sck_port[sck_bit] = 1
    if miso_pin_reg[miso_bit]:
        result = result | 0x08
    sck_port[sck_bit] = 0

    # Bit 2
    if data & 0x04:
        mosi_port[mosi_bit] = 1
    else:
        mosi_port[mosi_bit] = 0
    sck_port[sck_bit] = 1
    if miso_pin_reg[miso_bit]:
        result = result | 0x04
    sck_port[sck_bit] = 0

    # Bit 1
    if data & 0x02:
        mosi_port[mosi_bit] = 1
    else:
        mosi_port[mosi_bit] = 0
    sck_port[sck_bit] = 1
    if miso_pin_reg[miso_bit]:
        result = result | 0x02
    sck_port[sck_bit] = 0

    # Bit 0 (LSB)
    if data & 0x01:
        mosi_port[mosi_bit] = 1
    else:
        mosi_port[mosi_bit] = 0
    sck_port[sck_bit] = 1
    if miso_pin_reg[miso_bit]:
        result = result | 0x01
    sck_port[sck_bit] = 0

    return result
