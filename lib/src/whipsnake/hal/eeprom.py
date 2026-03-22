# -----------------------------------------------------------------------------
# Whipsnake Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the Whipsnake Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------

from whipsnake.types import uint8, uint16, inline
from whipsnake.chips import __CHIP__

class EEPROM:
    # Zero-cost EEPROM HAL.
    # Supports blocking byte-level read/write to internal EEPROM.
    #
    # ATmega328P: 1024 bytes, addresses 0x000-0x3FF
    # Write latency: ~3.4 ms (hardware-timed, polled)
    #
    # Usage:
    #   ee = EEPROM()
    #   ee.write(0x10, 0xAB)
    #   val: uint8 = ee.read(0x10)

    @inline
    def __init__(self):
        pass

    @inline
    def write(self, addr: uint16, value: uint8):
        match __CHIP__.name:
            case "atmega328p":
                from whipsnake.hal._eeprom.atmega328p import eeprom_write
                eeprom_write(addr, value)

    @inline
    def read(self, addr: uint16) -> uint8:
        match __CHIP__.name:
            case "atmega328p":
                from whipsnake.hal._eeprom.atmega328p import eeprom_read
                return eeprom_read(addr)
        return 0
