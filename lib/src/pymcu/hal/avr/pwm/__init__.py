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
from pymcu.types import uint8, uint16, inline, const
from pymcu.exceptions import CompileError

if __CHIP__.name == "attiny85" or __CHIP__.name == "attiny45" or __CHIP__.name == "attiny25":
    from pymcu.hal.avr.pwm.attiny85 import (
        pwm_init, pwm_select_ocr, pwm_select_tccr_b,
        pwm_select_start_val, pwm_prescaler_for_freq,
        pwm_connect, pwm_disconnect, pwm_release, pwm_clear_ocr_high,
        pwm_init_raw, pwm_u16_steps,
        pwm_uses_exact_t1, pwm_t1_exact_init, pwm_t1_exact_steps,
        pwm_t1_exact_write_ocr, pwm_t1_exact_start_val, pwm_t1_exact_frequency,
        pwm_t1_exact_is_off, pwm_bucket_frequency,
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
        pwm_connect, pwm_disconnect, pwm_release, pwm_clear_ocr_high,
        pwm_init_raw, pwm_u16_steps,
        pwm_uses_exact_t1, pwm_t1_exact_init, pwm_t1_exact_steps,
        pwm_t1_exact_write_ocr, pwm_t1_exact_start_val, pwm_t1_exact_frequency,
        pwm_t1_exact_is_off, pwm_bucket_frequency,
    )


class PWM:
    """Hardware PWM channel for AVR, zero-cost abstraction (all methods @inline)."""

    def __init__(self, pin: const, duty: uint8 = 0, freq: uint16 = 0, invert: const[uint8] = 0,
                 duty_u16: uint16 = 0):
        # duty is the 8-bit entry (0 off, 255 fully on); duty_u16, when non-zero, is the
        # 16-bit one every architecture's HAL takes (0..65535 = 0..100 %, what the
        # CircuitPython and MicroPython layers speak) and it wins over duty. The chip
        # module turns it into this channel's exact number of high counts.
        # invert selects the inverting compare output mode (COMxn0 set): the pin is
        # set on compare match and cleared at BOTTOM, so duty counts the LOW time.
        self._pin = pin
        self._invert = invert
        self._freq = freq
        # A Timer1 channel asking for a frequency the eight-bit buckets do not already give
        # exactly takes the mode whose TOP is a register, which reaches any frequency the
        # prescaler can divide to and whose compare registers are the full 16 bits. That is
        # what makes the servo idiom work: 50 Hz asked used to run at 61, and the duty had
        # 256 steps of 64 us where it now has 40 000 of 0.5. The predicate is a compile-time
        # constant, so a program asking for a bucket takes the path it always did.
        self._exact = pwm_uses_exact_t1(pin, freq)
        prescaler: uint8 = 0
        if self._exact:
            pwm_t1_exact_init(pin, freq, duty_u16, invert)
            prescaler = pwm_t1_exact_start_val(freq)
        else:
            if freq == 0:
                prescaler = pwm_select_start_val(pin)
            else:
                prescaler = pwm_prescaler_for_freq(pin, freq)
            if duty_u16 != 0:
                steps: uint16 = pwm_u16_steps(pin, duty_u16)
                if steps == 0:
                    pwm_init_raw(pin, 0, 1, prescaler, invert)
                else:
                    pwm_init_raw(pin, uint8(steps - 1), 0, prescaler, invert)
            else:
                pwm_init(pin, duty, prescaler, invert)
        self._ocr       = pwm_select_ocr(pin)
        self._tccr_b    = pwm_select_tccr_b(pin)
        self._start_val = prescaler

    @inline
    def set_duty(self, duty: uint8):
        # The 8-bit entry: 0 is off, 255 fully on, anything else high for duty + 1 of
        # 256 counts. 0 is off, not OCRx = 0: fast PWM with the compare register at
        # BOTTOM still emits a one-clock pulse every period. Disconnect the compare
        # output and drive the pin low instead, and reconnect it on the next duty. A
        # constant duty folds this to one path with no branch.
        if duty == 0:
            # The compare register goes to 0 as well, so that start() can tell an
            # off channel (nothing to reconnect) from a paused one by reading it back.
            pwm_clear_ocr_high(self._pin)
            self._ocr.value = 0
            pwm_disconnect(self._pin)
        else:
            # Timer1's compare registers are 16-bit and commit through a shared TEMP
            # byte, so the high byte has to be cleared immediately before the low one.
            # Folds to nothing on the 8-bit channels.
            pwm_clear_ocr_high(self._pin)
            self._ocr.value = duty
            pwm_connect(self._pin, self._invert)

    @inline
    def set_duty_u16(self, duty_u16: uint16):
        # The 16-bit entry, 0..65535 = 0..100 %, exact to this channel's resolution:
        # 32768 is 50.0 %, 65535 fully on, below half a count is off. Same register
        # traffic as set_duty(); written out rather than shared through a helper
        # method, because a method called from another method loses the const
        # binding of self._pin (it reached the chip module as a run-time value).
        if self._exact:
            # The whole 16-bit compare register, against a period of up to 65 536 counts.
            if pwm_t1_exact_steps(self._freq, duty_u16) == 0:
                pwm_t1_exact_write_ocr(self._pin, 0)
                pwm_disconnect(self._pin)
            else:
                pwm_t1_exact_write_ocr(self._pin, pwm_t1_exact_steps(self._freq, duty_u16))
                pwm_connect(self._pin, self._invert)
        else:
            steps: uint16 = pwm_u16_steps(self._pin, duty_u16)
            if steps == 0:
                pwm_clear_ocr_high(self._pin)
                self._ocr.value = 0
                pwm_disconnect(self._pin)
            else:
                pwm_clear_ocr_high(self._pin)
                self._ocr.value = uint8(steps - 1)
                pwm_connect(self._pin, self._invert)

    @inline
    def start(self):
        # The prescaler, then the compare output back on the pin. A duty of 0 stays
        # off: reconnecting it would emit the one-clock pulse OCRx = BOTTOM gives.
        self._tccr_b.value = self._start_val
        if self._exact:
            # The compare value is 16 bits here, so both bytes decide whether it is off: a
            # compare of 256 has a zero low byte and is not off.
            if pwm_t1_exact_is_off(self._pin) == 0:
                pwm_connect(self._pin, self._invert)
        else:
            if self._ocr.value != 0:
                pwm_connect(self._pin, self._invert)

    @inline
    def stop(self):
        # Off is this channel's compare output disconnected and the pin driven low,
        # the same as duty 0. It is NOT TCCRxB = 0: that stops the timer for the
        # sibling channel too, and on Timer0 for the time base behind monotonic()
        # and ticks_ms(), while the pin keeps whatever level the OCxA latch had when
        # the clock went away. Measured on an Arduino Uno: after deinit() D6 stayed
        # at 5 V about half the time (PyMCU#296).
        pwm_disconnect(self._pin)

    @inline
    def deinit(self):
        # stop(), and the pin back to an input without pull-up, which is what
        # CircuitPython leaves behind a deinit'd PWMOut.
        pwm_disconnect(self._pin)
        pwm_release(self._pin)

    @inline
    def set_freq(self, freq: uint16):
        # Retuning the timer retunes its other channel too; the selector claims the
        # prescaler on the way out, so a channel with a sibling is refused here.
        if self._exact:
            # The exact path's period is a register whose value comes from a division by the
            # frequency, and this frequency arrives at run time. Reprogramming it would need
            # that division in the emitted code, and the compare value would have to be
            # rescaled against the new period with it. Refused rather than half-done.
            raise CompileError(
                "a PWM running at an exact frequency cannot be retuned at run time. Its "
                "period lives in a register computed from the frequency, and so does every "
                "duty cycle measured against it, so changing one at run time needs a "
                "division this HAL does not emit. Construct the PWM at the frequency you "
                "want, or ask for one of the frequencies the fixed prescalers give "
                "(62500, 7812, 976, 244 or 61 Hz on this timer), which can be retuned.")
        self._start_val = pwm_prescaler_for_freq(self._pin, freq)
        self._tccr_b.value = self._start_val

    # The frequency this channel actually emits, which is not always the one asked for.
    @inline
    def frequency(self) -> uint16:
        if self._exact:
            return pwm_t1_exact_frequency(self._freq)
        return pwm_bucket_frequency(self._pin, self._freq)
