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

if __CHIP__.name == "attiny85" or __CHIP__.name == "attiny45" or __CHIP__.name == "attiny25":
    from pymcu.hal.avr.pwm.attiny85 import (
        pwm_init, pwm_select_ocr, pwm_select_tccr_b,
        pwm_select_start_val, pwm_prescaler_for_freq,
        pwm_connect, pwm_disconnect, pwm_clear_ocr_high,
    )
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
