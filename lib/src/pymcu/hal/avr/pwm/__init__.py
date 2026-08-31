# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
#
# AVR PWM facade -- pymcu.hal.avr.pwm
#
# Module-level conditional imports select the correct chip implementation.
# -----------------------------------------------------------------------------
from pymcu.chips import __CHIP__
from pymcu.types import uint8, uint16, inline
from pymcu.exceptions import CompileError

if __CHIP__.name == "attiny85" or __CHIP__.name == "attiny45" or __CHIP__.name == "attiny25":
    from pymcu.hal.avr.pwm.attiny85 import (
        pwm_init, pwm_select_ocr, pwm_select_tccr_b,
        pwm_select_start_val, pwm_prescaler_for_freq,
        pwm_connect, pwm_disconnect, pwm_clear_ocr_high,
    )
elif (__CHIP__.name == "atmega32u4" or __CHIP__.name == "attiny13" or __CHIP__.name == "attiny13a"
          or __CHIP__.name == "attiny2313" or __CHIP__.name == "attiny24"
          or __CHIP__.name == "attiny4313" or __CHIP__.name == "attiny44" or __CHIP__.name == "attiny84"):
    # Every one of these parts HAS timers, and none of them has the Timer2 this
    # implementation programs -- OCR2A, OCR2B, TCCR2A, TCCR2B. atmega32u4 is on the list and
    # is not an ATtiny: it has Timer0/1/3/4 and no Timer2, so reading this as an ATtiny
    # problem would have missed it. Falling through wrote Timer2 registers that do not exist.
    raise CompileError(
        "pymcu.hal.pwm has no implementation for this chip yet. The part HAS timers, but not "
        "the Timer2 this HAL programs, so there is no register map here that matches it. Use "
        "an ATmega 48/88/168/328 or 2560, or an ATtiny 25/45/85, for now.")
else:
    from pymcu.hal.avr.pwm.atmega328p import (
        pwm_init, pwm_select_ocr, pwm_select_tccr_b,
        pwm_select_start_val, pwm_prescaler_for_freq,
        pwm_connect, pwm_disconnect, pwm_clear_ocr_high,
    )


class PWM:
    """Hardware PWM channel for AVR, zero-cost abstraction (all methods @inline)."""

    def __init__(self, pin: str, duty: uint8, freq: uint16 = 0):
        self._pin = pin
        prescaler: uint8 = 0
        if freq == 0:
            prescaler = pwm_select_start_val(pin)
        else:
            prescaler = pwm_prescaler_for_freq(pin, freq)
        pwm_init(pin, duty, prescaler)
        self._ocr       = pwm_select_ocr(pin)
        self._tccr_b    = pwm_select_tccr_b(pin)
        self._start_val = prescaler

    @inline
    def set_duty(self, duty: uint8):
        # 0 is off, not OCRx = 0: fast PWM with the compare register at BOTTOM
        # still emits a one-clock pulse every period. Disconnect the compare
        # output and drive the pin low instead, and reconnect it on the next
        # non-zero duty. A constant duty folds this to one path with no branch.
        if duty == 0:
            pwm_disconnect(self._pin)
        else:
            # Timer1's compare registers are 16-bit and commit through a shared TEMP
            # byte, so the high byte has to be cleared immediately before the low one.
            # Folds to nothing on the 8-bit channels.
            pwm_clear_ocr_high(self._pin)
            self._ocr.value = duty
            pwm_connect(self._pin)

    @inline
    def start(self):
        self._tccr_b.value = self._start_val

    @inline
    def stop(self):
        self._tccr_b.value = 0x00

    @inline
    def set_freq(self, freq: uint16):
        self._start_val = pwm_prescaler_for_freq(self._pin, freq)
        self._tccr_b.value = self._start_val
