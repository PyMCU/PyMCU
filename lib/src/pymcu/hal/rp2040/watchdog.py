# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
#
# RP2040 watchdog HAL -- pymcu.hal.rp2040.watchdog
#
# The same surface as the AVR watchdog: Watchdog(timeout_ms), enable(), feed(). The counter drops
# by two per microsecond tick (erratum RP2040-E1), so a timeout of n ms loads n * 2000. The load
# register is 24 bits wide, which caps the timeout at 8388 ms; a longer request runs at that.

from pymcu.chips.rp2040 import WATCHDOG_CTRL, WATCHDOG_LOAD, WATCHDOG_CTRL_ENABLE
from pymcu.types import uint16, uint32, inline, const


@inline
def _load_for(timeout_ms: uint16) -> uint32:
    load: uint32 = uint32(timeout_ms) * 2000
    if load > 0xFFFFFF:
        load = 0xFFFFFF
    return load


class Watchdog:
    """Watchdog timer, zero-cost abstraction."""

    def __init__(self, timeout_ms: const[uint16] = 500):
        self._timeout_ms = timeout_ms
        self._load = _load_for(timeout_ms)

    @inline
    def enable(self):
        WATCHDOG_LOAD.value = self._load
        WATCHDOG_CTRL.value = WATCHDOG_CTRL.value | (1 << WATCHDOG_CTRL_ENABLE)

    @inline
    def arm_ms(self, timeout_ms: uint16):
        # Arm from a run-time timeout in ms: the one the CircuitPython wrapper holds in a field.
        self._load = _load_for(timeout_ms)
        WATCHDOG_LOAD.value = self._load
        WATCHDOG_CTRL.value = WATCHDOG_CTRL.value | (1 << WATCHDOG_CTRL_ENABLE)

    @inline
    def disable(self):
        WATCHDOG_CTRL.value = WATCHDOG_CTRL.value & ~(1 << WATCHDOG_CTRL_ENABLE)

    @inline
    def feed(self):
        WATCHDOG_LOAD.value = self._load
