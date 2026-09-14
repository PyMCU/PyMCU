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
    """Hardware PWM channel for PIC14, zero-cost abstraction (all methods @inline)."""

    def __init__(self, pin: str, duty: uint8 = 0, freq: uint16 = 0, duty_u16: uint16 = 0):
        # duty_u16 (0..65535 = 0..100 %, the 16-bit entry every architecture's HAL
        # takes) wins over the 8-bit duty when non-zero; this chip resolves it to its
        # 8-bit compare for now.
        self.pin = pin
        if duty_u16 != 0:
            duty = uint8(duty_u16 >> 8)
        match __CHIP__.name:
            case "pic16f18877":
                from pymcu.hal.pic14.pic16f18877_pwm import pwm_init
                pwm_init(pin, duty)
            case _:
                from pymcu.hal.pic14.pic16f877a_pwm import pwm_init
                pwm_init(pin, duty)

    @inline
    def set_duty(self, duty: uint8):
        match __CHIP__.name:
            case "pic16f18877":
                from pymcu.hal.pic14.pic16f18877_pwm import pwm_set_duty
                pwm_set_duty(self.pin, duty)
            case _:
                from pymcu.hal.pic14.pic16f877a_pwm import pwm_set_duty
                pwm_set_duty(self.pin, duty)

    @inline
    def set_duty_u16(self, duty_u16: uint16):
        # 16-bit entry, 0..65535 = 0..100 %, on this chip's 8-bit compare.
        self.set_duty(uint8(duty_u16 >> 8))

    @inline
    def start(self):
        match __CHIP__.name:
            case "pic16f18877":
                from pymcu.hal.pic14.pic16f18877_pwm import pwm_start
                pwm_start(self.pin)
            case _:
                from pymcu.hal.pic14.pic16f877a_pwm import pwm_start
                pwm_start(self.pin)

    @inline
    def stop(self):
        match __CHIP__.name:
            case "pic16f18877":
                from pymcu.hal.pic14.pic16f18877_pwm import pwm_stop
                pwm_stop(self.pin)
            case _:
                from pymcu.hal.pic14.pic16f877a_pwm import pwm_stop
                pwm_stop(self.pin)

    @inline
    def set_freq(self, freq: uint16):
        pass
