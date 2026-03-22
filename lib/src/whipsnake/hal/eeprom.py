# -----------------------------------------------------------------------------
# Whipsnake Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the Whipsnake Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------

from whipsnake.types import uint8, uint16, inline
from whipsnake.chips import __CHIP__


# noinspection PyProtectedMember
class EEPROM:
    # Zero-cost EEPROM HAL.
    # Supports blocking byte-level read/write to on-chip EEPROM.
    # Write operations poll until the hardware signals completion.
    # Address range and capacity depend on the target chip.
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
