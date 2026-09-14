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
from pymcu.exceptions import CompileError

if __CHIP__.name == "attiny85" or __CHIP__.name == "attiny45" or __CHIP__.name == "attiny25":
    from pymcu.hal.avr.eeprom.attiny85 import eeprom_write, eeprom_read
elif (__CHIP__.name == "attiny13" or __CHIP__.name == "attiny13a" or __CHIP__.name == "attiny2313"
          or __CHIP__.name == "attiny24" or __CHIP__.name == "attiny4313" or __CHIP__.name == "attiny44"
          or __CHIP__.name == "attiny84"):
    # These parts DO have EEPROM. They address it through EEAR, one 8-bit register, while
    # this implementation writes the EEARH/EEARL pair the ATmega has. Falling through to the
    # else wrote a high byte to an address that is not EEARH on these dies. The peripheral is
    # there and the driver is not, which is a different sentence from "this chip cannot".
    raise CompileError(
        "pymcu.hal.eeprom has no implementation for this chip yet. The part HAS EEPROM, but "
        "it addresses it through EEAR, a single 8-bit register, and this HAL drives the "
        "EEARH/EEARL pair the ATmega parts have. Use an ATmega or an ATtiny 25/45/85 for now.")
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

    # How many bytes this part's EEPROM holds, as a compile-time constant. A layer that
    # reports a size had nowhere to ask, so microcontroller.nvm reported the ATmega328P's
    # 1024 on every chip: an ATtiny85 has 512 and an ATmega2560 has 4096, and a program that
    # trusted len(nvm) wrote past the end of the first and used a quarter of the second.
    @inline
    def size(self) -> uint16:
        match __CHIP__.name:
            case "attiny13" | "attiny13a" | "attiny25":
                return 64
            case "attiny24" | "attiny2313" | "attiny45" | "attiny4313" | "attiny44":
                return 128
            case "attiny85" | "attiny84":
                return 512
            case "atmega48" | "atmega48p":
                return 256
            case "atmega88" | "atmega88p" | "atmega168" | "atmega168p":
                return 512
            case "atmega2560" | "atmega32u4":
                return 4096
            case _:
                return 1024
