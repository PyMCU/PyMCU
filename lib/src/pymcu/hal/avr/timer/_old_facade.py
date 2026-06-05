# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
from pymcu.chips import __CHIP__
from pymcu.types import uint8, uint16, uint32, const, inline, Callable


class Timer:
    """Hardware timer for AVR, zero-cost abstraction (all methods @inline).

    AVR supports timers 0, 1, 2 (atmega) or 0, 1 (attiny).
    Timer number n is a compile-time constant; both chip dispatch and
    timer-number dispatch fold at compile time.
    """

    IRQ_OVF   = 1
    IRQ_COMPA = 2

    def __init__(self, n: const[uint8], prescaler: uint16):
        self._n = n
        if n == 0:
            self._id = "t0"
        elif n == 1:
            self._id = "t1"
        elif n == 2:
            self._id = "t2"
        match __CHIP__.name:
            case "attiny85" | "attiny45" | "attiny25":
                match self._id:
                    case "t0":
                        from pymcu.hal.avr.attiny85_timer import timer0_init
                        timer0_init(prescaler)
                    case "t1":
                        from pymcu.hal.avr.attiny85_timer import timer1_init
                        timer1_init(prescaler)
            case _:
                match self._id:
                    case "t0":
                        from pymcu.hal.avr.atmega328p_timer import timer0_init
                        timer0_init(prescaler)
                    case "t1":
                        from pymcu.hal.avr.atmega328p_timer import timer1_init
                        timer1_init(prescaler)
                    case "t2":
                        from pymcu.hal.avr.atmega328p_timer import timer2_init
                        timer2_init(prescaler)

    @inline
    def start(self):
        match __CHIP__.name:
            case "attiny85" | "attiny45" | "attiny25":
                match self._id:
                    case "t0":
                        from pymcu.hal.avr.attiny85_timer import timer0_start
                        timer0_start()
                    case "t1":
                        from pymcu.hal.avr.attiny85_timer import timer1_start
                        timer1_start()
            case _:
                match self._id:
                    case "t0":
                        from pymcu.hal.avr.atmega328p_timer import timer0_start
                        timer0_start()
                    case "t1":
                        from pymcu.hal.avr.atmega328p_timer import timer1_start
                        timer1_start()
                    case "t2":
                        from pymcu.hal.avr.atmega328p_timer import timer2_start
                        timer2_start()

    @inline
    def stop(self):
        match __CHIP__.name:
            case "attiny85" | "attiny45" | "attiny25":
                match self._id:
                    case "t0":
                        from pymcu.hal.avr.attiny85_timer import timer0_stop
                        timer0_stop()
                    case "t1":
                        from pymcu.hal.avr.attiny85_timer import timer1_stop
                        timer1_stop()
            case _:
                match self._id:
                    case "t0":
                        from pymcu.hal.avr.atmega328p_timer import timer0_stop
                        timer0_stop()
                    case "t1":
                        from pymcu.hal.avr.atmega328p_timer import timer1_stop
                        timer1_stop()
                    case "t2":
                        from pymcu.hal.avr.atmega328p_timer import timer2_stop
                        timer2_stop()

    @inline
    def clear(self):
        match __CHIP__.name:
            case "attiny85" | "attiny45" | "attiny25":
                match self._id:
                    case "t0":
                        from pymcu.hal.avr.attiny85_timer import timer0_clear
                        timer0_clear()
                    case "t1":
                        from pymcu.hal.avr.attiny85_timer import timer1_clear
                        timer1_clear()
            case _:
                match self._id:
                    case "t0":
                        from pymcu.hal.avr.atmega328p_timer import timer0_clear
                        timer0_clear()
                    case "t1":
                        from pymcu.hal.avr.atmega328p_timer import timer1_clear
                        timer1_clear()
                    case "t2":
                        from pymcu.hal.avr.atmega328p_timer import timer2_clear
                        timer2_clear()

    @inline
    def set_compare(self, value: uint16):
        match __CHIP__.name:
            case "attiny85" | "attiny45" | "attiny25":
                match self._id:
                    case "t0":
                        from pymcu.hal.avr.attiny85_timer import timer0_set_compare
                        timer0_set_compare(value)
                    case "t1":
                        from pymcu.hal.avr.attiny85_timer import timer1_set_compare
                        timer1_set_compare(value)
            case _:
                match self._id:
                    case "t0":
                        from pymcu.hal.avr.atmega328p_timer import timer0_set_compare
                        timer0_set_compare(value)
                    case "t1":
                        from pymcu.hal.avr.atmega328p_timer import timer1_set_compare
                        timer1_set_compare(value)
                    case "t2":
                        from pymcu.hal.avr.atmega328p_timer import timer2_set_compare
                        timer2_set_compare(value)

    @inline
    def overflow(self) -> uint8:
        match __CHIP__.name:
            case "attiny85" | "attiny45" | "attiny25":
                match self._id:
                    case "t0":
                        from pymcu.hal.avr.attiny85_timer import timer0_overflow
                        return timer0_overflow()
                    case "t1":
                        from pymcu.hal.avr.attiny85_timer import timer1_overflow
                        return timer1_overflow()
            case _:
                match self._id:
                    case "t0":
                        from pymcu.hal.avr.atmega328p_timer import timer0_overflow
                        return timer0_overflow()
                    case "t1":
                        from pymcu.hal.avr.atmega328p_timer import timer1_overflow
                        return timer1_overflow()
                    case "t2":
                        from pymcu.hal.avr.atmega328p_timer import timer2_overflow
                        return timer2_overflow()
        return 0

    @inline
    def counter(self) -> uint16:
        match __CHIP__.name:
            case "attiny85" | "attiny45" | "attiny25":
                match self._id:
                    case "t0":
                        from pymcu.hal.avr.attiny85_timer import timer0_counter
                        return timer0_counter()
                    case "t1":
                        from pymcu.hal.avr.attiny85_timer import timer1_counter
                        return timer1_counter()
            case _:
                match self._id:
                    case "t0":
                        from pymcu.hal.avr.atmega328p_timer import timer0_counter
                        return timer0_counter()
                    case "t1":
                        from pymcu.hal.avr.atmega328p_timer import timer1_counter
                        return timer1_counter()
                    case "t2":
                        from pymcu.hal.avr.atmega328p_timer import timer2_counter
                        return timer2_counter()
        return 0

    @inline
    def irq(self, handler: Callable, mode: const = 1):
        match __CHIP__.name:
            case "attiny85" | "attiny45" | "attiny25":
                match self._id:
                    case "t0":
                        if mode == 2:
                            from pymcu.hal.avr.attiny85_timer import timer0_irq_compa_setup
                            timer0_irq_compa_setup(handler)
                        else:
                            from pymcu.hal.avr.attiny85_timer import timer0_irq_setup
                            timer0_irq_setup(handler)
                    case "t1":
                        if mode == 2:
                            from pymcu.hal.avr.attiny85_timer import timer1_irq_compa_setup
                            timer1_irq_compa_setup(handler)
                        else:
                            from pymcu.hal.avr.attiny85_timer import timer1_irq_setup
                            timer1_irq_setup(handler)
            case _:
                match self._id:
                    case "t0":
                        if mode == 2:
                            from pymcu.hal.avr.atmega328p_timer import timer0_irq_compa_setup
                            timer0_irq_compa_setup(handler)
                        else:
                            from pymcu.hal.avr.atmega328p_timer import timer0_irq_setup
                            timer0_irq_setup(handler)
                    case "t1":
                        if mode == 2:
                            from pymcu.hal.avr.atmega328p_timer import timer1_irq_compa_setup
                            timer1_irq_compa_setup(handler)
                        else:
                            from pymcu.hal.avr.atmega328p_timer import timer1_irq_setup
                            timer1_irq_setup(handler)
                    case "t2":
                        if mode == 2:
                            from pymcu.hal.avr.atmega328p_timer import timer2_irq_compa_setup
                            timer2_irq_compa_setup(handler)
                        else:
                            from pymcu.hal.avr.atmega328p_timer import timer2_irq_setup
                            timer2_irq_setup(handler)

    @inline
    def reinit(self, prescaler: uint16):
        match __CHIP__.name:
            case "attiny85" | "attiny45" | "attiny25":
                match self._id:
                    case "t0":
                        from pymcu.hal.avr.attiny85_timer import timer0_init
                        timer0_init(prescaler)
                    case "t1":
                        from pymcu.hal.avr.attiny85_timer import timer1_init
                        timer1_init(prescaler)
            case _:
                match self._id:
                    case "t0":
                        from pymcu.hal.avr.atmega328p_timer import timer0_init
                        timer0_init(prescaler)
                    case "t1":
                        from pymcu.hal.avr.atmega328p_timer import timer1_init
                        timer1_init(prescaler)
                    case "t2":
                        from pymcu.hal.avr.atmega328p_timer import timer2_init
                        timer2_init(prescaler)
