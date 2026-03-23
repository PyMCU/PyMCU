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
#
# softspi_transfer() uses a shift-left loop: data and result are shifted
# left each iteration so bit 7 is always the active bit, eliminating
# per-bit mask constants.
#
# half_us: half-period in microseconds computed from __FREQ__ and the
# target baudrate at construction time.  delay_us() is calibrated to
# __FREQ__, so the SCK frequency tracks the MCU clock automatically.
# When half_us == 0 (baudrate >= 1 MHz at 16 MHz) the guard folds at
# compile time and both delay_us calls are eliminated entirely.
# -----------------------------------------------------------------------------

from whipsnake.types import uint8, inline
from whipsnake.hal.gpio import Pin
from whipsnake.time import delay_us


@inline
def softspi_transfer(sck: Pin, mosi: Pin, miso: Pin, half_us: uint8, data: uint8) -> uint8:
    # SPI Mode 0, MSB-first.
    # tx is a local copy of data so the caller's variable is not modified.
    # Shift tx left each iteration so bit 7 is always the active bit;
    # shift result left to accumulate received bits from MSB to LSB.
    tx: uint8 = data
    result: uint8 = 0
    i: uint8 = 0
    while i < 8:
        if tx & 0x80:
            mosi.high()
        else:
            mosi.low()
        if half_us > 0:
            delay_us(half_us)
        sck.high()
        result = result << 1
        if miso.value():
            result = result | 1
        if half_us > 0:
            delay_us(half_us)
        sck.low()
        tx = tx << 1
        i = i + 1
    return result
