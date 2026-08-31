# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
#
# AVR Tone facade -- pymcu.hal.avr.tone
# Module-level conditional import selects the chip implementation.
# -----------------------------------------------------------------------------
from pymcu.chips import __CHIP__
from pymcu.types import uint16, inline
from pymcu.exceptions import CompileError

if __CHIP__.name == "atmega2560":
    from pymcu.hal.avr.tone.atmega2560 import tone_start, no_tone
elif __CHIP__.name == "atmega32u4":
    from pymcu.hal.avr.tone.atmega32u4 import tone_start, no_tone
elif (__CHIP__.name == "attiny13" or __CHIP__.name == "attiny13a" or __CHIP__.name == "attiny2313"
          or __CHIP__.name == "attiny24" or __CHIP__.name == "attiny25" or __CHIP__.name == "attiny4313"
          or __CHIP__.name == "attiny44" or __CHIP__.name == "attiny45" or __CHIP__.name == "attiny84"
          or __CHIP__.name == "attiny85"):
    # EVERY ATtiny in the tree, not just the ones other modules leave out: this module's own
    # `if` covers only atmega2560 and atmega32u4, so 25/45/85 fall through here as well even
    # though they get a branch elsewhere. All ten want OCR2A, TCCR2A, TCCR2B and TIMSK2, and
    # not one of them has a Timer2. They do have Timer0 and Timer1, so tone COULD be built on
    # them; it has not been.
    raise CompileError(
        "pymcu.hal.tone has no implementation for this chip yet. No ATtiny has the Timer2 this "
        "HAL programs. The part CAN make a tone -- it has Timer0 and Timer1 -- but PyMCU has no "
        "driver for that here yet. Use an ATmega 328/2560/32u4 for now.")
else:
    from pymcu.hal.avr.tone.atmega328p import tone_start, no_tone


@inline
def tone(freq_hz: uint16):
    tone_start(freq_hz)


@inline
def noTone():
    no_tone()
