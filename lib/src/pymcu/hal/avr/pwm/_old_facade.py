# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
from pymcu.chips import __CHIP__
from pymcu.types import uint8, uint16, inline


class PWM:
    """Hardware PWM channel for AVR, zero-cost abstraction (all methods @inline)."""

    def __init__(self, pin: str, duty: uint8, freq: uint16 = 0):
        match __CHIP__.name:
            case "attiny85" | "attiny45" | "attiny25":
                from pymcu.hal.avr.attiny85_pwm import pwm_init, pwm_select_ocr, pwm_select_tccr_b, pwm_select_start_val, pwm_prescaler_for_freq
                prescaler: uint8 = 0
                if freq == 0:
                    prescaler = pwm_select_start_val(pin)
                else:
                    prescaler = pwm_prescaler_for_freq(pin, freq)
                self._pin       = pin
                pwm_init(pin, duty, prescaler)
                self._ocr       = pwm_select_ocr(pin)
                self._tccr_b    = pwm_select_tccr_b(pin)
                self._start_val = prescaler
            case _:
                from pymcu.hal.avr.atmega328p_pwm import pwm_init, pwm_select_ocr, pwm_select_tccr_b, pwm_select_start_val, pwm_prescaler_for_freq
                prescaler: uint8 = 0
                if freq == 0:
                    prescaler = pwm_select_start_val(pin)
                else:
                    prescaler = pwm_prescaler_for_freq(pin, freq)
                self._pin       = pin
                pwm_init(pin, duty, prescaler)
                self._ocr       = pwm_select_ocr(pin)
                self._tccr_b    = pwm_select_tccr_b(pin)
                self._start_val = prescaler

    @inline
    def set_duty(self, duty: uint8):
        self._ocr.value = duty

    @inline
    def start(self):
        self._tccr_b.value = self._start_val

    @inline
    def stop(self):
        self._tccr_b.value = 0x00

    @inline
    def set_freq(self, freq: uint16):
        match __CHIP__.name:
            case "attiny85" | "attiny45" | "attiny25":
                from pymcu.hal.avr.attiny85_pwm import pwm_prescaler_for_freq
                self._start_val = pwm_prescaler_for_freq(self._pin, freq)
                self._tccr_b.value = self._start_val
            case _:
                from pymcu.hal.avr.atmega328p_pwm import pwm_prescaler_for_freq
                self._start_val = pwm_prescaler_for_freq(self._pin, freq)
                self._tccr_b.value = self._start_val
