# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
#
# tone HAL for ATmega2560 -- OC2A is on PB4 (Arduino Mega pin 10)
from pymcu.chips.atmega2560 import TCCR2A, TCCR2B, TIMSK2, OCR2A
from pymcu.chips.atmega2560 import DDRB, PORTB
from pymcu.chips import __FREQ__
from pymcu.types import uint8, uint16, uint32, inline


@inline
def _tone_ocr(freq_hz: uint16, prescaler: uint16) -> uint8:
    half_counts: uint32 = __FREQ__ // (2 * prescaler * freq_hz)
    if half_counts < 1:
        return 1
    if half_counts > 255:
        return 255
    return half_counts - 1


@inline
def tone_start(freq_hz: uint16):
    DDRB[4] = 1         # PB4 as output (OC2A on ATmega2560)
    TIMSK2.value = 0
    TCCR2A.value = 0x42 # COM2A0=1, WGM21=1 (CTC)

    if freq_hz >= 7813:
        OCR2A.value = _tone_ocr(freq_hz, 1)
        TCCR2B.value = 0x01
    elif freq_hz >= 977:
        OCR2A.value = _tone_ocr(freq_hz, 8)
        TCCR2B.value = 0x02
    elif freq_hz >= 489:
        OCR2A.value = _tone_ocr(freq_hz, 32)
        TCCR2B.value = 0x03
    elif freq_hz >= 244:
        OCR2A.value = _tone_ocr(freq_hz, 64)
        TCCR2B.value = 0x04
    elif freq_hz >= 122:
        OCR2A.value = _tone_ocr(freq_hz, 128)
        TCCR2B.value = 0x05
    elif freq_hz >= 61:
        OCR2A.value = _tone_ocr(freq_hz, 256)
        TCCR2B.value = 0x06
    else:
        OCR2A.value = _tone_ocr(freq_hz, 1024)
        TCCR2B.value = 0x07


@inline
def no_tone():
    TCCR2B.value = 0x00
    TCCR2A.value = 0x00
    PORTB[4] = 0
