# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------

from pymcu.types import uint8, uint16, inline
from pymcu.chips import __CHIP__


# noinspection PyProtectedMember
class EEPROM:
    """On-chip EEPROM, zero-cost abstraction (all methods @inline).

    Provides blocking byte-level read and write access to the
    microcontroller's internal EEPROM. Write operations poll until the
    hardware signals completion. Address range and capacity depend on the
    target chip.

    Usage::

        ee = EEPROM()
        ee.write(0x10, 0xAB)
        val: uint8 = ee.read(0x10)
    """

    def __init__(self):
        """Initialize the EEPROM peripheral."""
        pass

    @inline
    def write(self, addr: uint16, value: uint8):
        """Write one byte to EEPROM at the given address.

        Blocks until any previous write completes before starting the new
        write, then waits for the new write to finish.
        """
        match __CHIP__.name:
            case "atmega328p" | "atmega328" | "atmega168p" | "atmega168" | "atmega88p" | "atmega88" | "atmega48p" | "atmega48":
                from pymcu.hal._eeprom.atmega328p import eeprom_write
                eeprom_write(addr, value)

    @inline
    def read(self, addr: uint16) -> uint8:
        """Read one byte from EEPROM at the given address.

        Blocks until any in-progress write completes, then returns the
        stored byte.
        """
        match __CHIP__.name:
            case "atmega328p" | "atmega328" | "atmega168p" | "atmega168" | "atmega88p" | "atmega88" | "atmega48p" | "atmega48":
                from pymcu.hal._eeprom.atmega328p import eeprom_read
                return eeprom_read(addr)

        return 0
