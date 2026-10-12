# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
#
# RISC-V timer facade -- pymcu.hal.riscv.timer
#
# The general-purpose timers (TIM1, TIM2) have no HAL yet; what exists is the millis()/micros()
# time base on the CH32V003's SysTick (pymcu.hal.riscv.timer.ch32v003). The class is here so
# `from pymcu.hal import Timer` resolves on every RISC-V part, and it refuses where it is built.
# -----------------------------------------------------------------------------
from pymcu.exceptions import CompileError
from pymcu.types import uint8, uint16, const, inline, Callable


class Timer:
    """Not implemented for RISC-V parts yet: constructing one refuses."""

    IRQ_OVF   = 1
    IRQ_COMPA = 2

    def __init__(self, n: const[uint8], prescaler: uint16):
        raise CompileError(
            "pymcu.hal.Timer is not implemented for RISC-V parts yet. millis() and micros() "
            "work on the CH32V003 (they run on SysTick).")

    @inline
    def start(self):
        pass

    @inline
    def stop(self):
        pass

    @inline
    def clear(self):
        pass

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
        pass
