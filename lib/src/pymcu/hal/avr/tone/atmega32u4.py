# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
#
# tone HAL for ATmega32U4
#
# The ATmega32U4 does not have Timer2.  tone() uses Timer3 OC3A on PC6
# (Arduino Leonardo / Micro pin 5).
from pymcu.chips.atmega32u4 import TCCR3A, TCCR3B, TIMSK3, OCR3AL, OCR3AH
from pymcu.chips.atmega32u4 import DDRC, PORTC
from pymcu.chips import __FREQ__
from pymcu.types import uint8, uint16, uint32, inline


@inline
def _tone_ocr16(freq_hz: uint16, prescaler: uint16) -> uint16:
    half_counts: uint32 = __FREQ__ // (2 * prescaler * freq_hz)
    if half_counts < 1:
        return 1
    if half_counts > 65535:
        return 65535
    return half_counts - 1


@inline
def tone_start(freq_hz: uint16):
    DDRC[6] = 1         # PC6 as output (OC3A on ATmega32U4 / Arduino Leonardo D5)
    TIMSK3.value = 0
    # COM3A0=1 (toggle OC3A), WGM32=1 (CTC mode B)
    TCCR3A.value = 0x40
    ocr: uint16 = _tone_ocr16(freq_hz, 64)
    hi: uint8 = uint8(ocr >> 8)
    OCR3AH.value = hi
    lo: uint8 = uint8(ocr)
    OCR3AL.value = lo
    TCCR3B.value = 0x0B   # WGM32=1, prescaler 64


@inline
def no_tone():
    TCCR3B.value = 0x00
    TCCR3A.value = 0x00
    PORTC[6] = 0
