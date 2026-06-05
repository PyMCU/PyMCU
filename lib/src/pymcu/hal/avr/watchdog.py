# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
from pymcu.chips import __CHIP__
from pymcu.types import uint8, uint16, inline, const


class Watchdog:
    """Hardware watchdog timer, zero-cost abstraction (all methods @inline)."""

    def __init__(self, timeout_ms: const[uint16] = 500):
        self._timeout_ms = timeout_ms

    @inline
    def enable(self):
        match __CHIP__.name:
            case "attiny85" | "attiny45" | "attiny25":
                from pymcu.hal.avr.attiny85_watchdog import wdt_timeout_wdp, wdt_enable
                wdp: uint8 = wdt_timeout_wdp(self._timeout_ms)
                wdt_enable(wdp)
            case _:
                from pymcu.hal.avr.atmega328p_watchdog import wdt_timeout_wdp, wdt_enable
                wdp: uint8 = wdt_timeout_wdp(self._timeout_ms)
                wdt_enable(wdp)

    @inline
    def disable(self):
        match __CHIP__.name:
            case "attiny85" | "attiny45" | "attiny25":
                from pymcu.hal.avr.attiny85_watchdog import wdt_disable
                wdt_disable()
            case _:
                from pymcu.hal.avr.atmega328p_watchdog import wdt_disable
                wdt_disable()

    @inline
    def feed(self):
        match __CHIP__.name:
            case "attiny85" | "attiny45" | "attiny25":
                from pymcu.hal.avr.attiny85_watchdog import wdt_feed
                wdt_feed()
            case _:
                from pymcu.hal.avr.atmega328p_watchdog import wdt_feed
                wdt_feed()
