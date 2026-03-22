# -----------------------------------------------------------------------------
# Whipsnake Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the Whipsnake Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------

from whipsnake.types import uint8, uint16, inline, const
from whipsnake.chips import __CHIP__


# noinspection PyProtectedMember
class Watchdog:
    """Hardware watchdog timer, zero-cost abstraction (all methods @inline).

    Resets the microcontroller if feed() is not called within the configured
    timeout period. Available timeout values depend on the target chip's
    watchdog prescaler options.

    Usage::

        wdt = Watchdog(timeout_ms=500)
        wdt.enable()
        while True:
            do_work()
            wdt.feed()    # must call within timeout_ms or the MCU resets
    """

    @inline
    def __init__(self, timeout_ms: const[uint16] = 500):
        """Store the desired watchdog timeout in milliseconds."""
        self._timeout_ms = timeout_ms

    @inline
    def enable(self):
        """Enable the watchdog timer with the configured timeout."""
        match __CHIP__.name:
            case "atmega328p":
                from whipsnake.hal._watchdog.atmega328p import wdt_enable, wdt_timeout_wdp
                wdp: uint8 = wdt_timeout_wdp(self._timeout_ms)
                wdt_enable(wdp)

    @inline
    def disable(self):
        """Disable the watchdog timer."""
        match __CHIP__.name:
            case "atmega328p":
                from whipsnake.hal._watchdog.atmega328p import wdt_disable
                wdt_disable()

    @inline
    def feed(self):
        """Reset the watchdog counter (pet the dog).

        Must be called within the configured timeout or the MCU will reset.
        """
        # Reset the watchdog counter. Must be called within the configured timeout.
        match __CHIP__.name:
            case "atmega328p":
                from whipsnake.hal._watchdog.atmega328p import wdt_feed
                wdt_feed()
