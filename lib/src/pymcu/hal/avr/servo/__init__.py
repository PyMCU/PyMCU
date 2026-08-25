# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
#
# AVR Servo facade -- pymcu.hal.avr.servo
#
# ATmega328P is the only supported chip for now (Timer1 Fast PWM).
# -----------------------------------------------------------------------------
from pymcu.chips import __CHIP__
from pymcu.exceptions import CompileError
from pymcu.types import uint8, uint16, const, inline

if __CHIP__.name == "atmega328p" or __CHIP__.name == "atmega328" or __CHIP__.name == "atmega168p" or __CHIP__.name == "atmega168" or __CHIP__.name == "atmega88p" or __CHIP__.name == "atmega88" or __CHIP__.name == "atmega48p" or __CHIP__.name == "atmega48":
    from pymcu.hal.avr.servo.atmega328p import (
        servo_init, servo_write_a, servo_write_b,
        servo_write_us_a, servo_write_us_b, servo_stop,
    )


class Servo:
    """RC servo motor, zero-cost abstraction (all methods @inline).

    pin is a compile-time constant selecting the PWM channel (PB1 or PB2).
    Uses Timer1 in Fast PWM mode 14 (TOP=ICR1) at 50 Hz.
    """

    def __init__(self, pin: const[str]):
        self._pin = pin
        match __CHIP__.name:
            case "atmega328p" | "atmega328" | "atmega168p" | "atmega168" | "atmega88p" | "atmega88" | "atmega48p" | "atmega48":
                # Timer1 drives OC1A (PB1) and OC1B (PB2) and nothing else. Any
                # other pin used to reach servo_init(), which took Timer1 over,
                # and then matched no channel in write(), so OCR1A kept its
                # power-on value and the pin was never touched.
                match pin:
                    case "PB1" | "PB2":
                        servo_init()
                    case _:
                        raise CompileError("Servo: unsupported pin -- use PB1 (OC1A) or PB2 (OC1B)")

    @inline
    def write(self, degrees: uint8):
        match __CHIP__.name:
            case "atmega328p" | "atmega328" | "atmega168p" | "atmega168" | "atmega88p" | "atmega88" | "atmega48p" | "atmega48":
                match self._pin:
                    case "PB1":
                        servo_write_a(degrees)
                    case "PB2":
                        servo_write_b(degrees)
                    case _:
                        raise CompileError("Servo: unsupported pin -- use PB1 (OC1A) or PB2 (OC1B)")

    @inline
    def write_microseconds(self, us: uint16):
        match __CHIP__.name:
            case "atmega328p" | "atmega328" | "atmega168p" | "atmega168" | "atmega88p" | "atmega88" | "atmega48p" | "atmega48":
                match self._pin:
                    case "PB1":
                        servo_write_us_a(us)
                    case "PB2":
                        servo_write_us_b(us)
                    case _:
                        raise CompileError("Servo: unsupported pin -- use PB1 (OC1A) or PB2 (OC1B)")

    @inline
    def detach(self):
        match __CHIP__.name:
            case "atmega328p" | "atmega328" | "atmega168p" | "atmega168" | "atmega88p" | "atmega88" | "atmega48p" | "atmega48":
                servo_stop()
