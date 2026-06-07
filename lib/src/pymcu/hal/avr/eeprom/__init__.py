# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
#
# AVR EEPROM facade -- pymcu.hal.avr.eeprom
#
# Module-level conditional import selects the correct chip implementation at
# compile time. Only the winning branch is loaded into the dependency graph.
# -----------------------------------------------------------------------------
from pymcu.chips import __CHIP__
from pymcu.types import uint8, uint16, inline

if __CHIP__.name == "attiny85" or __CHIP__.name == "attiny45" or __CHIP__.name == "attiny25":
    from pymcu.hal.avr.eeprom.attiny85 import eeprom_write, eeprom_read
else:
    from pymcu.hal.avr.eeprom.atmega328p import eeprom_write, eeprom_read


class EEPROM:
    """On-chip EEPROM, zero-cost abstraction (all methods @inline)."""

    def __init__(self):
        pass

    @inline
    def write(self, addr: uint16, value: uint8):
        eeprom_write(addr, value)

    @inline
    def read(self, addr: uint16) -> uint8:
        return eeprom_read(addr)
