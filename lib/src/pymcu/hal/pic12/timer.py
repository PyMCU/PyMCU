# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
from pymcu.exceptions import CompileError
from pymcu.types import uint8, uint16, const, inline, Callable


class Timer:
    """Hardware timer for PIC12, zero-cost abstraction (all methods @inline).

    The baseline core has one 8-bit timer, no interrupt hardware and no compare
    unit, so most of the portable Timer surface cannot exist here. It refuses to
    compile rather than accepting the call and doing nothing.
    """

    IRQ_OVF   = 1
    IRQ_COMPA = 2

    def __init__(self, n: const[uint8], prescaler: uint16):
        self._n = n
        self._id = "t0"
        from pymcu.hal.pic12.pic10f200_timer import timer0_init
        timer0_init(prescaler)

    @inline
    def start(self):
        pass

    @inline
    def stop(self):
        raise CompileError("PIC12 Timer0 runs from reset off the instruction clock and has "
                           "no enable bit; stop() cannot be honoured on this core")

    @inline
    def clear(self):
        from pymcu.hal.pic12.pic10f200_timer import timer0_clear
        timer0_clear()

    @inline
    def set_compare(self, value: uint16):
        raise CompileError("PIC12 has no timer compare hardware")

    @inline
    def overflow(self) -> uint8:
        raise CompileError("PIC12 has no T0IF flag: an overflow is only observable by "
                           "polling counter() and watching it wrap")

    @inline
    def counter(self) -> uint16:
        from pymcu.hal.pic12.pic10f200_timer import timer0_read
        return uint16(timer0_read())

    @inline
    def irq(self, handler: Callable, mode: const = 1):
        raise CompileError("PIC12 has no interrupt hardware")

    @inline
    def reinit(self, prescaler: uint16):
        from pymcu.hal.pic12.pic10f200_timer import timer0_init
        timer0_init(prescaler)
