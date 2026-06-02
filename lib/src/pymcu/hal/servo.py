# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
#
# hal/servo.py -- RC servo motor control (Arduino-compatible)
#
# Generates standard RC servo signals: 50 Hz, 1 ms--2 ms pulse width.
#
# Usage (ATmega328P):
#
#   from pymcu.hal.servo import Servo
#
#   s = Servo("PB1")      # OC1A channel -- Arduino D9
#   s.write(90)           # center position (90 degrees)
#   s.write_microseconds(1500)   # same, expressed in microseconds
#
# Supported pins (ATmega328P / Arduino Uno):
#   OC1A = PB1 = Arduino D9   (channel A)
#   OC1B = PB2 = Arduino D10  (channel B)
#
# Both channels share Timer1 and the same prescaler, so both instances must
# use the same Timer1 configuration.  A maximum of two Servo instances are
# supported per ATmega328P sketch.
#
# Conflict: Servo uses Timer1.  Do NOT mix with:
#   - PWM on OC1A (D9) or OC1B (D10)
#   - Any Timer(1, ...) instance
#   - millis()/micros() if they are configured to use Timer1
from pymcu.chips import __CHIP__
from pymcu.types import uint8, uint16, const, inline


# noinspection PyProtectedMember
class Servo:
    """RC servo motor, zero-cost abstraction (all methods @inline).

    ``pin`` is a compile-time constant that selects the PWM channel.
    Only OC1A and OC1B output pins are supported (see module docstring).

    Servo uses Timer1 in Fast PWM mode 14 (TOP=ICR1) at 50 Hz.
    Call write() or write_microseconds() to set position.
    """

    def __init__(self, pin: const[str]):
        self._pin = pin
        match __CHIP__.name:
            case "atmega328p" | "atmega328" | "atmega168p" | "atmega168" | "atmega88p" | "atmega88" | "atmega48p" | "atmega48":
                from pymcu.hal._servo.atmega328p import servo_init
                servo_init()

    @inline
    def write(self, degrees: uint8):
        """Set servo position in degrees (0--180)."""
        match __CHIP__.name:
            case "atmega328p" | "atmega328" | "atmega168p" | "atmega168" | "atmega88p" | "atmega88" | "atmega48p" | "atmega48":
                from pymcu.hal._servo.atmega328p import servo_write_a, servo_write_b
                match self._pin:
                    case "PB1":
                        servo_write_a(degrees)
                    case "PB2":
                        servo_write_b(degrees)

    @inline
    def write_microseconds(self, us: uint16):
        """Set servo pulse width in microseconds (1000--2000)."""
        match __CHIP__.name:
            case "atmega328p" | "atmega328" | "atmega168p" | "atmega168" | "atmega88p" | "atmega88" | "atmega48p" | "atmega48":
                from pymcu.hal._servo.atmega328p import servo_write_us_a, servo_write_us_b
                match self._pin:
                    case "PB1":
                        servo_write_us_a(us)
                    case "PB2":
                        servo_write_us_b(us)

    @inline
    def detach(self):
        """Stop the servo signal and release Timer1."""
        match __CHIP__.name:
            case "atmega328p" | "atmega328" | "atmega168p" | "atmega168" | "atmega88p" | "atmega88" | "atmega48p" | "atmega48":
                from pymcu.hal._servo.atmega328p import servo_stop
                servo_stop()
