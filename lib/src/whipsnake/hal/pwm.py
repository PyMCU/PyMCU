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
# Supported architectures: AVR, PIC.
#
# PWM(pin, duty) accepts a port-pin name string (e.g. "PD6").
# If you hold a Pin instance, pass pin.name -- it is a compile-time const[str]
# in the ZCA alias chain with no runtime cost: PWM(my_pin.name, duty).
#
# The timer channel, compare register, and control register pointers are
# resolved at construction time; set_duty() / start() / stop() each
# compile to a single register write with no further dispatch.
#
# duty: 0-255 (0 = 0%, 255 = 100%). Timer is left stopped after init;
# call start() before the first set_duty().
from whipsnake.chips import __CHIP__
from whipsnake.types import uint8, inline


# noinspection PyProtectedMember
class PWM:
    """Hardware PWM channel, zero-cost abstraction (all methods @inline).

    Accepts a port-pin name string. If you hold a Pin instance, pass pin.name:

        pwm = PWM("PD6", 0)
        pwm = PWM(led_pin.name, 0)     # identical generated code
        pwm.start()
        pwm.set_duty(128)    # 50% duty cycle
    """

    def __init__(self, pin: str, duty: uint8):
        """Initialize a hardware PWM channel from a port-pin name string.

        duty: initial duty cycle, 0-255 (0 = 0%, 255 = 100%).
        The timer is left stopped after init; call start() before set_duty().
        """
        match __CHIP__.name:
            case "atmega328p" | "atmega328" | "atmega168p" | "atmega168" | "atmega88p" | "atmega88" | "atmega48p" | "atmega48":
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

    @inline
    def set_duty(self, duty: uint8):
        """Update the duty cycle. duty: 0-255 (0 = 0%, 255 = 100%).

        Compiles to a single register write.
        """
        match __CHIP__.name:
            case "atmega328p" | "atmega328" | "atmega168p" | "atmega168" | "atmega88p" | "atmega88" | "atmega48p" | "atmega48":
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

    @inline
    def start(self):
        """Enable the timer clock and start generating the PWM waveform.

        Must be called once before the first set_duty() takes effect.
        """
        match __CHIP__.name:
            case "atmega328p" | "atmega328" | "atmega168p" | "atmega168" | "atmega88p" | "atmega88" | "atmega48p" | "atmega48":
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

    @inline
    def stop(self):
        """Disable the timer clock and stop the PWM waveform.

        The duty cycle value is preserved; start() resumes at the same level.
        """
        match __CHIP__.name:
            case "atmega328p" | "atmega328" | "atmega168p" | "atmega168" | "atmega88p" | "atmega88" | "atmega48p" | "atmega48":
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
