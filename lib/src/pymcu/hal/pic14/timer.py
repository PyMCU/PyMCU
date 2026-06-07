# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
from pymcu.chips import __CHIP__
from pymcu.types import uint8, uint16, const, inline, Callable


class Timer:
    """Hardware timer for PIC14, zero-cost abstraction (all methods @inline)."""

    IRQ_OVF   = 1
    IRQ_COMPA = 2

    def __init__(self, n: const[uint8], prescaler: uint16):
        self._n = n
        self._id = "t0"
        match __CHIP__.name:
            case "pic16f18877":
                from pymcu.hal.pic14.pic16f18877_timer import timer0_init
                timer0_init(prescaler)
            case "pic16f84a":
                from pymcu.hal.pic14.pic16f84a_timer import timer0_init
                timer0_init(prescaler)
            case _:
                from pymcu.hal.pic14.pic16f877a_timer import timer0_init
                timer0_init(prescaler)

    @inline
    def start(self):
        match __CHIP__.name:
            case "pic16f18877":
                from pymcu.hal.pic14.pic16f18877_timer import timer0_start
                timer0_start()
            case "pic16f84a":
                from pymcu.hal.pic14.pic16f84a_timer import timer0_start
                timer0_start()
            case _:
                from pymcu.hal.pic14.pic16f877a_timer import timer0_start
                timer0_start()

    @inline
    def stop(self):
        match __CHIP__.name:
            case "pic16f18877":
                from pymcu.hal.pic14.pic16f18877_timer import timer0_stop
                timer0_stop()
            case "pic16f84a":
                from pymcu.hal.pic14.pic16f84a_timer import timer0_stop
                timer0_stop()
            case _:
                from pymcu.hal.pic14.pic16f877a_timer import timer0_stop
                timer0_stop()

    @inline
    def clear(self):
        match __CHIP__.name:
            case "pic16f18877":
                from pymcu.hal.pic14.pic16f18877_timer import timer0_clear
                timer0_clear()
            case "pic16f84a":
                from pymcu.hal.pic14.pic16f84a_timer import timer0_clear
                timer0_clear()
            case _:
                from pymcu.hal.pic14.pic16f877a_timer import timer0_clear
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
        match __CHIP__.name:
            case "pic16f18877":
                from pymcu.hal.pic14.pic16f18877_timer import timer0_init
                timer0_init(prescaler)
            case "pic16f84a":
                from pymcu.hal.pic14.pic16f84a_timer import timer0_init
                timer0_init(prescaler)
            case _:
                from pymcu.hal.pic14.pic16f877a_timer import timer0_init
                timer0_init(prescaler)
