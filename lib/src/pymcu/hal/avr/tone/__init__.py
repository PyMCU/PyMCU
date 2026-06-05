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

if __CHIP__.name == "atmega2560":
    from pymcu.hal.avr.tone.atmega2560 import tone_start, no_tone
elif __CHIP__.name == "atmega32u4":
    from pymcu.hal.avr.tone.atmega32u4 import tone_start, no_tone
else:
    from pymcu.hal.avr.tone.atmega328p import tone_start, no_tone


@inline
def tone(freq_hz: uint16):
    tone_start(freq_hz)


@inline
def noTone():
    no_tone()
