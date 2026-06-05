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

if __CHIP__.name == "attiny85" or __CHIP__.name == "attiny45" or __CHIP__.name == "attiny25":
    from pymcu.hal.avr.watchdog.attiny85 import wdt_timeout_wdp, wdt_enable, wdt_disable, wdt_feed
else:
    from pymcu.hal.avr.watchdog.atmega328p import wdt_timeout_wdp, wdt_enable, wdt_disable, wdt_feed


class Watchdog:
    """Hardware watchdog timer, zero-cost abstraction (all methods @inline)."""

    def __init__(self, timeout_ms: const[uint16] = 500):
        self._timeout_ms = timeout_ms

    @inline
    def enable(self):
        wdp: uint8 = wdt_timeout_wdp(self._timeout_ms)
        wdt_enable(wdp)

    @inline
    def disable(self):
        wdt_disable()

    @inline
    def feed(self):
        wdt_feed()
