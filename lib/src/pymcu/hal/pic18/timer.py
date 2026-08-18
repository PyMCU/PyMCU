# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
from pymcu.types import uint8, uint16, const, inline, Callable
from pymcu.exceptions import CompileError


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
    def overflow(self) -> uint8:
        from pymcu.hal.pic18.pic18f45k50_timer import timer0_overflow
        return timer0_overflow()

    @inline
    def clear_overflow(self):
        from pymcu.hal.pic18.pic18f45k50_timer import timer0_clear_overflow
        timer0_clear_overflow()

    @inline
    def counter(self) -> uint16:
        from pymcu.hal.pic18.pic18f45k50_timer import timer0_counter
        return timer0_counter()

    @inline
    def set_compare(self, value: uint16):
        raise CompileError("Timer0 on the PIC18F45K50 has no compare register; poll counter() or use the overflow flag")

    @inline
    def irq(self, handler: Callable, mode: const = 1):
        raise CompileError("Timer.irq is not wired for PIC18; decorate the handler with @interrupt and enable TMR0IE in INTCON")

    @inline
    def reinit(self, prescaler: uint16):
        from pymcu.hal.pic18.pic18f45k50_timer import timer0_init
        timer0_init(prescaler)
