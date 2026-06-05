# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
#
# hal/tone.py -- square-wave tone generation (Arduino-compatible)
#
# tone(freq_hz)   -- start a continuous square wave on the tone pin
# noTone()        -- stop the square wave and silence the pin
#
# Tone pin by chip:
#   ATmega328P/168/88/48 : PB3  (Arduino D11)
#   ATmega2560            : PB4  (Arduino Mega D10)
#   ATmega32U4            : PC6  (Arduino Leonardo D5)
from pymcu.chips import __CHIP__
from pymcu.exceptions import CompileError
from pymcu.types import uint16, inline


@inline
def tone(freq_hz: uint16):
    match __CHIP__.arch:
        case "avr":
            match __CHIP__.name:
                case "atmega2560":
                    from pymcu.hal.avr.atmega2560_tone import tone_start
                    tone_start(freq_hz)
                case "atmega32u4":
                    from pymcu.hal.avr.atmega32u4_tone import tone_start
                    tone_start(freq_hz)
                case _:
                    from pymcu.hal.avr.atmega328p_tone import tone_start
                    tone_start(freq_hz)
        case _:
            raise CompileError("Tone not supported on this architecture")


@inline
def noTone():
    match __CHIP__.arch:
        case "avr":
            match __CHIP__.name:
                case "atmega2560":
                    from pymcu.hal.avr.atmega2560_tone import no_tone
                    no_tone()
                case "atmega32u4":
                    from pymcu.hal.avr.atmega32u4_tone import no_tone
                    no_tone()
                case _:
                    from pymcu.hal.avr.atmega328p_tone import no_tone
                    no_tone()
        case _:
            raise CompileError("Tone not supported on this architecture")
