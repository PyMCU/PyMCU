# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
#
# AVR Watchdog facade -- pymcu.hal.avr.watchdog
# Module-level conditional import selects the chip implementation.
# -----------------------------------------------------------------------------
from pymcu.chips import __CHIP__
from pymcu.types import uint8, uint16, inline, const
from pymcu.exceptions import CompileError

if __CHIP__.name == "attiny85" or __CHIP__.name == "attiny45" or __CHIP__.name == "attiny25":
    from pymcu.hal.avr.watchdog.attiny85 import wdt_timeout_wdp, wdt_enable, wdt_disable, wdt_feed, wdt_arm_rt
elif (__CHIP__.name == "attiny13" or __CHIP__.name == "attiny13a"):
    # The ATtiny 13/13a DO have a watchdog. Theirs is WDTCR; this implementation writes
    # WDTCSR, which those dies do not have. Peripheral present, driver absent.
    raise CompileError(
        "pymcu.hal.watchdog has no implementation for this chip yet. The ATtiny 13/13a HAVE a "
        "watchdog, but it is WDTCR and this HAL drives the WDTCSR the ATmega parts have. Use "
        "an ATmega or an ATtiny 25/45/85 for now.")
else:
    from pymcu.hal.avr.watchdog.atmega328p import wdt_timeout_wdp, wdt_enable, wdt_disable, wdt_feed, wdt_arm_rt


class Watchdog:
    """Hardware watchdog timer, zero-cost abstraction (all methods @inline)."""

    def __init__(self, timeout_ms: const[uint16] = 500):
        self._timeout_ms = timeout_ms

    @inline
    def enable(self):
        wdp: uint8 = wdt_timeout_wdp(self._timeout_ms)
        wdt_enable(wdp)

    @inline
    def arm_ms(self, timeout_ms: uint16):
        # Arm from a RUNTIME timeout in ms (the const-free path). Used by the
        # CircuitPython microcontroller.watchdog wrapper, whose timeout is a
        # mutable instance member rather than a compile-time constant.
        wdt_arm_rt(timeout_ms)

    @inline
    def disable(self):
        wdt_disable()

    @inline
    def feed(self):
        wdt_feed()
