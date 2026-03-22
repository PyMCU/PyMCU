# -----------------------------------------------------------------------------
# Whipsnake Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the Whipsnake Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------

from whipsnake.types import uint8, uint16, inline, const
from whipsnake.chips import __CHIP__

class Watchdog:
    # Zero-cost Watchdog Timer HAL.
    # Generates a system reset if firmware does not call feed() within timeout.
    #
    # ATmega328P timeouts (WDP prescaler, ~3V/25C):
    #   16ms, 32ms, 64ms, 125ms, 250ms, 500ms, 1s, 2s, 4s, 8s
    #
    # Usage:
    #   wdt = Watchdog(timeout_ms=500)
    #   wdt.enable()
    #   while True:
    #       do_work()
    #       wdt.feed()    # must call within 500ms or MCU resets

    @inline
    def __init__(self, timeout_ms: const[uint16] = 500):
        self._timeout_ms = timeout_ms

    @inline
    def enable(self):
        match __CHIP__.name:
            case "atmega328p":
                from whipsnake.hal._watchdog.atmega328p import wdt_enable, wdt_timeout_wdp
                wdp: uint8 = wdt_timeout_wdp(self._timeout_ms)
                wdt_enable(wdp)

    @inline
    def disable(self):
        match __CHIP__.name:
            case "atmega328p":
                from whipsnake.hal._watchdog.atmega328p import wdt_disable
                wdt_disable()

    @inline
    def feed(self):
        # Reset the watchdog counter. Must be called within the configured timeout.
        match __CHIP__.name:
            case "atmega328p":
                from whipsnake.hal._watchdog.atmega328p import wdt_feed
                wdt_feed()
