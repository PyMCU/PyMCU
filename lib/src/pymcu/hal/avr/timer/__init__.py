# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
#
# AVR Timer facade -- pymcu.hal.avr.timer
#
# Module-level conditional imports select the chip implementation.
# Timer-number dispatch (self._n) folds at compile time because n is const.
# -----------------------------------------------------------------------------
from pymcu.chips import __CHIP__
from pymcu.types import uint8, uint16, uint32, const, inline, Callable

if __CHIP__.name == "attiny85" or __CHIP__.name == "attiny45" or __CHIP__.name == "attiny25":
    from pymcu.hal.avr.timer.attiny85 import (
        timer0_init, timer0_start, timer0_stop, timer0_clear, timer0_counter,
        timer0_overflow, timer0_set_compare, timer0_irq_setup, timer0_irq_compa_setup,
        timer1_init, timer1_start, timer1_stop, timer1_clear, timer1_counter,
        timer1_overflow, timer1_set_compare, timer1_irq_setup, timer1_irq_compa_setup,
        millis_init, millis,
    )
else:
    from pymcu.hal.avr.timer.atmega328p import (
        timer0_init, timer0_start, timer0_stop, timer0_clear, timer0_counter,
        timer0_overflow, timer0_set_compare, timer0_irq_setup, timer0_irq_compa_setup,
        timer1_init, timer1_start, timer1_stop, timer1_clear, timer1_counter,
        timer1_overflow, timer1_set_compare, timer1_irq_setup, timer1_irq_compa_setup,
        timer2_init, timer2_start, timer2_stop, timer2_clear, timer2_counter,
        timer2_overflow, timer2_set_compare, timer2_irq_setup, timer2_irq_compa_setup,
        millis_init, millis,
    )


class Timer:
    """Hardware timer for AVR, zero-cost abstraction (all methods @inline).

    n is a compile-time constant; both chip and timer-number dispatch fold.
    """

    IRQ_OVF   = 1
    IRQ_COMPA = 2

    def __init__(self, n: const[uint8], prescaler: uint16):
        self._n = n
        match n:
            case 0:
                timer0_init(prescaler)
            case 1:
                timer1_init(prescaler)
            case 2:
                timer2_init(prescaler)

    @inline
    def start(self):
        match self._n:
            case 0:
                timer0_start()
            case 1:
                timer1_start()
            case 2:
                timer2_start()

    @inline
    def stop(self):
        match self._n:
            case 0:
                timer0_stop()
            case 1:
                timer1_stop()
            case 2:
                timer2_stop()

    @inline
    def clear(self):
        match self._n:
            case 0:
                timer0_clear()
            case 1:
                timer1_clear()
            case 2:
                timer2_clear()

    @inline
    def set_compare(self, value: uint16):
        match self._n:
            case 0:
                timer0_set_compare(value)
            case 1:
                timer1_set_compare(value)
            case 2:
                timer2_set_compare(value)

    @inline
    def overflow(self) -> uint8:
        match self._n:
            case 0:
                return timer0_overflow()
            case 1:
                return timer1_overflow()
            case 2:
                return timer2_overflow()
        return 0

    @inline
    def counter(self) -> uint16:
        match self._n:
            case 0:
                return timer0_counter()
            case 1:
                return timer1_counter()
            case 2:
                return timer2_counter()
        return 0

    @inline
    def irq(self, handler: Callable, mode: const = 1):
        match self._n:
            case 0:
                if mode == 2:
                    timer0_irq_compa_setup(handler)
                else:
                    timer0_irq_setup(handler)
            case 1:
                if mode == 2:
                    timer1_irq_compa_setup(handler)
                else:
                    timer1_irq_setup(handler)
            case 2:
                if mode == 2:
                    timer2_irq_compa_setup(handler)
                else:
                    timer2_irq_setup(handler)

    @inline
    def reinit(self, prescaler: uint16):
        match self._n:
            case 0:
                timer0_init(prescaler)
            case 1:
                timer1_init(prescaler)
            case 2:
                timer2_init(prescaler)
