# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
#
# hal/timer.py -- unified Timer ZCA + millis free-running counter
#
# Timer(n, prescaler) -- n is compile-time; all methods @inline.
# The arch and chip dispatch fold at compile time, emitting only the
# instructions for the selected architecture and timer number.
#
# AVR: timers 0, 1, 2 (atmega) or 0, 1 (attiny).
# PIC: only n=0 (Timer0).
#
# millis_init() / millis() are ATmega-only; they are no-ops on ATtiny85.
from pymcu.chips import __CHIP__
from pymcu.exceptions import CompileError

if __CHIP__.arch == "avr":
    from pymcu.hal.avr.timer import Timer
    if __CHIP__.name == "attiny85" or __CHIP__.name == "attiny45" or __CHIP__.name == "attiny25":
        from pymcu.hal.avr.timer.attiny85 import millis_init, millis, micros
    elif __CHIP__.name == "attiny2313" or __CHIP__.name == "attiny4313":
        # These fell through to the ATmega's module, which programs TIMSK0 and TIFR0. Neither
        # part declares either register, so what got emitted was the ATmega328P's 0x6E on a
        # die where that address is not a timer-interrupt mask. The build was clean.
        #
        # They DO have a Timer0, so this is missing work and not absent hardware, and the
        # message says so. Same shape as the five HALs in #238.
        raise CompileError(
            "pymcu.time has no millisecond clock for this chip yet. The ATtiny 2313 and 4313 "
            "have a Timer0, but their interrupt mask and flag registers are not the ATmega's "
            "TIMSK0/TIFR0 that this implementation programs, so there is no driver for them "
            "here. delay_ms() and delay_us() work; millis() and micros() do not.")
    else:
        from pymcu.hal.avr.timer.atmega328p import millis_init, millis, micros
elif __CHIP__.arch == "pic14":
    from pymcu.hal.pic14.timer import Timer
elif __CHIP__.arch == "pic18":
    from pymcu.hal.pic18.timer import Timer
    from pymcu.hal.pic18.pic18f45k50_timer import millis_init, millis, micros
elif __CHIP__.arch == "pic12":
    from pymcu.hal.pic12.timer import Timer
else:
    raise CompileError("Timer not supported on this architecture")
