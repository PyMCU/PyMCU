# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
from pymcu.chips import __CHIP__
from pymcu.types import uint8, uint16, inline


class EEPROM:
    """On-chip EEPROM, zero-cost abstraction (all methods @inline)."""

    def __init__(self):
        pass

    @inline
    def write(self, addr: uint16, value: uint8):
        match __CHIP__.name:
            case "attiny85" | "attiny45" | "attiny25":
                from pymcu.hal.avr.attiny85_eeprom import eeprom_write
                eeprom_write(addr, value)
            case _:
                from pymcu.hal.avr.atmega328p_eeprom import eeprom_write
                eeprom_write(addr, value)

    @inline
    def read(self, addr: uint16) -> uint8:
        match __CHIP__.name:
            case "attiny85" | "attiny45" | "attiny25":
                from pymcu.hal.avr.attiny85_eeprom import eeprom_read
                return eeprom_read(addr)
            case _:
                from pymcu.hal.avr.atmega328p_eeprom import eeprom_read
                return eeprom_read(addr)
