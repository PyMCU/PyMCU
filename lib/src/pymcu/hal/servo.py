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
from pymcu.chips import __CHIP__
from pymcu.exceptions import CompileError

if __CHIP__.arch == "avr":
    from pymcu.hal.avr.servo import Servo
else:
    raise CompileError("Servo not supported on this architecture")
