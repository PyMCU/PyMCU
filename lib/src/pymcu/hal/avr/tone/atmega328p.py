# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
#
# tone HAL for ATmega328P -- hardware square-wave generation on OC2A (PB3/D11)
#
# Uses Timer2 in CTC mode with hardware pin toggle (COM2A0=1).
# The timer hardware automatically toggles PB3/D11 on every compare match,
# producing a clean square wave with zero CPU overhead and perfect timing.
#
# Prescaler selection for maximum frequency accuracy:
#   freq >= 7813 Hz  -> prescaler 1    (OCR2A = F_CPU / (2 * freq * 1)   - 1)
#   freq >= 977 Hz   -> prescaler 8
#   freq >= 489 Hz   -> prescaler 32
#   freq >= 244 Hz   -> prescaler 64
#   freq >= 122 Hz   -> prescaler 128
#   freq >= 61 Hz    -> prescaler 256
#   freq >= 31 Hz    -> prescaler 1024
#
# Conflict: tone() uses Timer2.  Do NOT mix with PWM on OC2A/OC2B or any
# other Timer2 usage while tone() is active.

from pymcu.chips.atmega328p import TCCR2A, TCCR2B, TIMSK2, OCR2A
from pymcu.chips.atmega328p import DDRB, PORTB
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
    """Start a continuous square wave on OC2A (PB3 / Arduino D11).

    Configures Timer2 in CTC mode with hardware pin toggle.  freq_hz must be
    a value in [31, 65535] Hz at 16 MHz; the actual frequency is rounded to
    the nearest achievable value given the available prescalers.

    The prescaler is chosen at compile time from the constant freq_hz value.
    If freq_hz is a runtime variable, the compiler selects prescaler 64 as a
    reasonable default (usable in the range 244--7812 Hz).
    """
    DDRB[3] = 1         # PB3 as output (OC2A)
    TIMSK2.value = 0    # disable Timer2 interrupts (hardware toggle, no ISR)
    TCCR2A.value = 0x42 # COM2A0=1 (toggle OC2A), WGM21=1 (CTC)

    if freq_hz >= 7813:
        OCR2A.value = _tone_ocr(freq_hz, 1)
        TCCR2B.value = 0x01   # prescaler 1
    elif freq_hz >= 977:
        OCR2A.value = _tone_ocr(freq_hz, 8)
        TCCR2B.value = 0x02   # prescaler 8
    elif freq_hz >= 489:
        OCR2A.value = _tone_ocr(freq_hz, 32)
        TCCR2B.value = 0x03   # prescaler 32
    elif freq_hz >= 244:
        OCR2A.value = _tone_ocr(freq_hz, 64)
        TCCR2B.value = 0x04   # prescaler 64
    elif freq_hz >= 122:
        OCR2A.value = _tone_ocr(freq_hz, 128)
        TCCR2B.value = 0x05   # prescaler 128
    elif freq_hz >= 61:
        OCR2A.value = _tone_ocr(freq_hz, 256)
        TCCR2B.value = 0x06   # prescaler 256
    else:
        OCR2A.value = _tone_ocr(freq_hz, 1024)
        TCCR2B.value = 0x07   # prescaler 1024


@inline
def no_tone():
    """Stop the square wave and silence OC2A (PB3 / Arduino D11).

    Stops Timer2 and drives PB3 low.  Safe to call if tone() was never
    started (Timer2 is already stopped).
    """
    TCCR2B.value = 0x00   # stop Timer2
    TCCR2A.value = 0x00   # disconnect OC2A
    PORTB[3] = 0          # drive pin low (silence buzzer)
