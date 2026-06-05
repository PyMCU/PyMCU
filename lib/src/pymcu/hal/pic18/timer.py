# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
from pymcu.types import uint8, uint16, const, inline, Callable


class Timer:
    """Hardware timer for PIC18, zero-cost abstraction (all methods @inline)."""

    IRQ_OVF   = 1
    IRQ_COMPA = 2

    def __init__(self, n: const[uint8], prescaler: uint16):
        self._n = n
        self._id = "t0"
        from pymcu.hal.pic18.pic18f45k50_timer import timer0_init
        timer0_init(prescaler)

    @inline
    def start(self):
        from pymcu.hal.pic18.pic18f45k50_timer import timer0_start
        timer0_start()

    @inline
    def stop(self):
        from pymcu.hal.pic18.pic18f45k50_timer import timer0_stop
        timer0_stop()

    @inline
    def clear(self):
        from pymcu.hal.pic18.pic18f45k50_timer import timer0_clear
        timer0_clear()

    @inline
    def set_compare(self, value: uint16):
        pass

    @inline
    def overflow(self) -> uint8:
        return 0

    @inline
    def counter(self) -> uint16:
        return 0

    @inline
    def irq(self, handler: Callable, mode: const = 1):
        pass

    @inline
    def reinit(self, prescaler: uint16):
        from pymcu.hal.pic18.pic18f45k50_timer import timer0_init
        timer0_init(prescaler)
