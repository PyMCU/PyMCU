# -----------------------------------------------------------------------------
# Whipsnake Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the Whipsnake Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
#
# hal/pwm.py -- hardware PWM zero-cost abstraction (ZCA)
#
# Supported architectures: AVR (ATmega328P), PIC (16F877A, 16F18877, 18F45K50).
#
# ATmega328P timer/pin mapping:
#   PD6 -> OC0A (Timer0)   PD5 -> OC0B (Timer0)
#   PB1 -> OC1A (Timer1)   PB2 -> OC1B (Timer1)
#   PB3 -> OC2A (Timer2)   PD3 -> OC2B (Timer2)
#
# ATmega328P implementation details:
#   self._ocr       -- compile-time ptr to the Output Compare Register (OCRxN).
#   self._tccr_b    -- compile-time ptr to TCCRxB (controls the timer clock).
#   self._start_val -- TCCRxB value that enables the timer with the configured prescaler.
#   All three are resolved at construction via compile-time match/case and stored
#   so that set_duty() / start() / stop() each compile to a single register write.
#
# PIC implementation:
#   self.pin -- compile-time const[str] pin name forwarded to arch-specific helpers.
from whipsnake.chips import __CHIP__
from whipsnake.types import uint8, inline
from whipsnake.hal.gpio import Pin


# noinspection PyProtectedMember
class PWM:

    # Initialise a hardware PWM channel.
    # pin:  port-pin name string, e.g. "PD6" (ATmega328P OC0A).
    # duty: initial duty cycle, 0-255 (0 = 0 %, 255 = 100 %).
    # The timer is left stopped after init; call start() before set_duty().
    @inline
    def __init__(self, pin: str, duty: uint8):
        match __CHIP__.name:
            case "atmega328p":
                from whipsnake.hal._pwm.atmega328p import pwm_init, pwm_select_ocr, pwm_select_tccr_b, pwm_select_start_val
                pwm_init(pin, duty)
                self._ocr       = pwm_select_ocr(pin)
                self._tccr_b    = pwm_select_tccr_b(pin)
                self._start_val = pwm_select_start_val(pin)
            case "pic16f877a":
                from whipsnake.hal._pwm.pic16f877a import pwm_init
                self.pin = pin
                pwm_init(pin, duty)
            case "pic16f18877":
                from whipsnake.hal._pwm.pic16f18877 import pwm_init
                self.pin = pin
                pwm_init(pin, duty)
            case "pic18f45k50":
                from whipsnake.hal._pwm.pic18f45k50 import pwm_init
                self.pin = pin
                pwm_init(pin, duty)

    # Initialise a hardware PWM channel from a Pin ZCA instance.
    # Extracts pin.name at compile time via the ZCA alias chain -- no runtime cost.
    # duty: initial duty cycle, 0-255 (0 = 0 %, 255 = 100 %).
    # The timer is left stopped after init; call start() before set_duty().
    @inline
    def __init__(self, pin: Pin, duty: uint8):
        match __CHIP__.name:
            case "atmega328p":
                from whipsnake.hal._pwm.atmega328p import pwm_init, pwm_select_ocr, pwm_select_tccr_b, pwm_select_start_val
                pwm_init(pin.name, duty)
                self._ocr       = pwm_select_ocr(pin.name)
                self._tccr_b    = pwm_select_tccr_b(pin.name)
                self._start_val = pwm_select_start_val(pin.name)
            case "pic16f877a":
                from whipsnake.hal._pwm.pic16f877a import pwm_init
                self.pin = pin.name
                pwm_init(pin.name, duty)
            case "pic16f18877":
                from whipsnake.hal._pwm.pic16f18877 import pwm_init
                self.pin = pin.name
                pwm_init(pin.name, duty)
            case "pic18f45k50":
                from whipsnake.hal._pwm.pic18f45k50 import pwm_init
                self.pin = pin.name
                pwm_init(pin.name, duty)

    # Update the duty cycle.
    # duty: 0-255 (0 = 0 %, 255 = 100 %).
    # ATmega328P: compiles to a single OUT/STS instruction writing OCRxN.
    @inline
    def set_duty(self, duty: uint8):
        match __CHIP__.name:
            case "atmega328p":
                # Direct OCR write -- single STS instruction.
                self._ocr.value = duty
            case "pic16f877a":
                from whipsnake.hal._pwm.pic16f877a import pwm_set_duty
                pwm_set_duty(self.pin, duty)
            case "pic16f18877":
                from whipsnake.hal._pwm.pic16f18877 import pwm_set_duty
                pwm_set_duty(self.pin, duty)
            case "pic18f45k50":
                from whipsnake.hal._pwm.pic18f45k50 import pwm_set_duty
                pwm_set_duty(self.pin, duty)

    # Enable the timer clock (start generating the PWM waveform).
    # ATmega328P: restores TCCRxB to the prescaler value selected at init.
    # Must be called once before the first set_duty() takes effect.
    @inline
    def start(self):
        match __CHIP__.name:
            case "atmega328p":
                # Restore prescaler value to re-enable the timer.
                self._tccr_b.value = self._start_val
            case "pic16f877a":
                from whipsnake.hal._pwm.pic16f877a import pwm_start
                pwm_start(self.pin)
            case "pic16f18877":
                from whipsnake.hal._pwm.pic16f18877 import pwm_start
                pwm_start(self.pin)
            case "pic18f45k50":
                from whipsnake.hal._pwm.pic18f45k50 import pwm_start
                pwm_start(self.pin)

    # Disable the timer clock (stop the PWM waveform).
    # ATmega328P: clears TCCRxB to 0x00; the OCR value is preserved.
    # A subsequent start() resumes the waveform at the same duty cycle.
    @inline
    def stop(self):
        match __CHIP__.name:
            case "atmega328p":
                # Clear TCCRxB to stop the timer clock.
                self._tccr_b.value = 0x00
            case "pic16f877a":
                from whipsnake.hal._pwm.pic16f877a import pwm_stop
                pwm_stop(self.pin)
            case "pic16f18877":
                from whipsnake.hal._pwm.pic16f18877 import pwm_stop
                pwm_stop(self.pin)
            case "pic18f45k50":
                from whipsnake.hal._pwm.pic18f45k50 import pwm_stop
                pwm_stop(self.pin)
