# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
#
# hal/pwm.py -- hardware PWM zero-cost abstraction (ZCA)
#
# PWM(pin, duty) accepts a port-pin name string (e.g. "PD6").
# The timer channel and compare register are resolved at construction time;
# set_duty() / start() / stop() each compile to a single register write.
from pymcu.chips import __CHIP__
from pymcu.exceptions import CompileError

if __CHIP__.arch == "avr":
    from pymcu.hal.avr.pwm import PWM
elif __CHIP__.arch == "pic14":
    from pymcu.hal.pic14.pwm import PWM
elif __CHIP__.arch == "pic18":
    from pymcu.hal.pic18.pwm import PWM
else:
    raise CompileError("PWM not supported on this architecture")
