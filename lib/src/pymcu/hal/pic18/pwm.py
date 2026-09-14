# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
from pymcu.types import uint8, uint16, inline


class PWM:
    """Hardware PWM channel for PIC18, zero-cost abstraction (all methods @inline)."""

    def __init__(self, pin: str, duty: uint8 = 0, freq: uint16 = 0, duty_u16: uint16 = 0):
        # duty_u16 (0..65535 = 0..100 %, the 16-bit entry every architecture's HAL
        # takes) wins over the 8-bit duty when non-zero; this chip resolves it to its
        # 8-bit compare for now.
        self.pin = pin
        if duty_u16 != 0:
            duty = uint8(duty_u16 >> 8)
        from pymcu.hal.pic18.pic18f45k50_pwm import pwm_init
        pwm_init(pin, duty, freq)

    @inline
    def set_duty(self, duty: uint8):
        from pymcu.hal.pic18.pic18f45k50_pwm import pwm_set_duty
        pwm_set_duty(self.pin, duty)

    @inline
    def set_duty_u16(self, duty_u16: uint16):
        # 16-bit entry, 0..65535 = 0..100 %, on this chip's 8-bit compare.
        self.set_duty(uint8(duty_u16 >> 8))

    @inline
    def start(self):
        from pymcu.hal.pic18.pic18f45k50_pwm import pwm_start
        pwm_start(self.pin)

    @inline
    def stop(self):
        from pymcu.hal.pic18.pic18f45k50_pwm import pwm_stop
        pwm_stop(self.pin)

    @inline
    def set_freq(self, freq: uint16):
        from pymcu.hal.pic18.pic18f45k50_pwm import pwm_set_freq
        pwm_set_freq(self.pin, freq)
