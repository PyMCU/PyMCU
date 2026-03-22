# -----------------------------------------------------------------------------
# Whipsnake Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the Whipsnake Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------

from whipsnake.chips import __CHIP__
from whipsnake.types import uint8, uint16, const, inline

# ---- Unified Timer ZCA ----
# Timer(n, prescaler) -- n is a compile-time constant; all methods @inline.
# The compiler folds both the chip dispatch and the timer-number dispatch at
# compile time, emitting only the instructions for the selected timer.
#
# AVR supports multiple numbered hardware timers (n=0, 1, 2).
# PIC chips only support n=0 (Timer0).
# Available prescaler values, resolution, and overflow rates depend on the
# target chip and the selected timer.
# CTC mode (compare-match interrupt) is configured via set_compare().

# noinspection PyProtectedMember
class Timer:

    @inline
    def __init__(self, n: const[uint8], prescaler: uint8):
        self._n = n
        match __CHIP__.name:
            case "atmega328p":
                match n:
                    case 0:
                        from whipsnake.hal._timer.atmega328p import timer0_init
                        timer0_init(prescaler)
                    case 1:
                        from whipsnake.hal._timer.atmega328p import timer1_init
                        timer1_init(prescaler)
                    case 2:
                        from whipsnake.hal._timer.atmega328p import timer2_init
                        timer2_init(prescaler)
            case "pic16f877a":
                from whipsnake.hal._timer.pic16f877a import timer0_init
                timer0_init(prescaler)
            case "pic16f18877":
                from whipsnake.hal._timer.pic16f18877 import timer0_init
                timer0_init(prescaler)
            case "pic16f84a":
                from whipsnake.hal._timer.pic16f84a import timer0_init
                timer0_init(prescaler)
            case "pic10f200":
                from whipsnake.hal._timer.pic10f200 import timer0_init
                timer0_init(prescaler)
            case "pic18f45k50":
                from whipsnake.hal._timer.pic18f45k50 import timer0_init
                timer0_init(prescaler)

    @inline
    def start(self):
        match __CHIP__.name:
            case "atmega328p":
                match self._n:
                    case 0:
                        from whipsnake.hal._timer.atmega328p import timer0_start
                        timer0_start()
                    case 1:
                        from whipsnake.hal._timer.atmega328p import timer1_start
                        timer1_start()
                    case 2:
                        from whipsnake.hal._timer.atmega328p import timer2_start
                        timer2_start()
            case "pic16f877a":
                from whipsnake.hal._timer.pic16f877a import timer0_start
                timer0_start()
            case "pic16f18877":
                from whipsnake.hal._timer.pic16f18877 import timer0_start
                timer0_start()
            case "pic16f84a":
                from whipsnake.hal._timer.pic16f84a import timer0_start
                timer0_start()
            case "pic10f200":
                from whipsnake.hal._timer.pic10f200 import timer0_start
                timer0_start()
            case "pic18f45k50":
                from whipsnake.hal._timer.pic18f45k50 import timer0_start
                timer0_start()

    @inline
    def stop(self):
        match __CHIP__.name:
            case "atmega328p":
                match self._n:
                    case 0:
                        from whipsnake.hal._timer.atmega328p import timer0_stop
                        timer0_stop()
                    case 1:
                        from whipsnake.hal._timer.atmega328p import timer1_stop
                        timer1_stop()
                    case 2:
                        from whipsnake.hal._timer.atmega328p import timer2_stop
                        timer2_stop()
            case "pic16f877a":
                from whipsnake.hal._timer.pic16f877a import timer0_stop
                timer0_stop()
            case "pic16f18877":
                from whipsnake.hal._timer.pic16f18877 import timer0_stop
                timer0_stop()
            case "pic16f84a":
                from whipsnake.hal._timer.pic16f84a import timer0_stop
                timer0_stop()
            case "pic10f200":
                from whipsnake.hal._timer.pic10f200 import timer0_stop
                timer0_stop()
            case "pic18f45k50":
                from whipsnake.hal._timer.pic18f45k50 import timer0_stop
                timer0_stop()

    @inline
    def clear(self):
        match __CHIP__.name:
            case "atmega328p":
                match self._n:
                    case 0:
                        from whipsnake.hal._timer.atmega328p import timer0_clear
                        timer0_clear()
                    case 1:
                        from whipsnake.hal._timer.atmega328p import timer1_clear
                        timer1_clear()
                    case 2:
                        from whipsnake.hal._timer.atmega328p import timer2_clear
                        timer2_clear()
            case "pic16f877a":
                from whipsnake.hal._timer.pic16f877a import timer0_clear
                timer0_clear()
            case "pic16f18877":
                from whipsnake.hal._timer.pic16f18877 import timer0_clear
                timer0_clear()
            case "pic16f84a":
                from whipsnake.hal._timer.pic16f84a import timer0_clear
                timer0_clear()
            case "pic10f200":
                from whipsnake.hal._timer.pic10f200 import timer0_clear
                timer0_clear()
            case "pic18f45k50":
                from whipsnake.hal._timer.pic18f45k50 import timer0_clear
                timer0_clear()

    # Sets the OCR (Output Compare Register) and enables CTC mode.
    # CTC vectors (ATmega328P): Timer0_COMPA=0x001C, Timer1_COMPA=0x0016,
    #   Timer2_COMPA=0x000E.
    # Call start() first to configure the prescaler, then set_compare().
    @inline
    def set_compare(self, value: uint16):
        match __CHIP__.name:
            case "atmega328p":
                match self._n:
                    case 0:
                        from whipsnake.hal._timer.atmega328p import timer0_set_compare
                        timer0_set_compare(value)
                    case 1:
                        from whipsnake.hal._timer.atmega328p import timer1_set_compare
                        timer1_set_compare(value)
                    case 2:
                        from whipsnake.hal._timer.atmega328p import timer2_set_compare
                        timer2_set_compare(value)

    @inline
    def overflow(self) -> uint8:
        match __CHIP__.name:
            case "atmega328p":
                match self._n:
                    case 0:
                        from whipsnake.hal._timer.atmega328p import timer0_overflow
                        return timer0_overflow()
                    case 1:
                        from whipsnake.hal._timer.atmega328p import timer1_overflow
                        return timer1_overflow()
                    case 2:
                        from whipsnake.hal._timer.atmega328p import timer2_overflow
                        return timer2_overflow()
        return 0
