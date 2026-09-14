# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
#
# Counting edges on a pin -- pymcu.hal.counter
#
#   flow = EdgeCounter("PD2", edge=2)      # falling edges
#   pulses = flow.count()
#   flow.reset()
#
# A flow meter, a tachometer, an encoder wheel with one track: anything whose reading is
# how many times a line changed. What does the counting is the chip's business.
# -----------------------------------------------------------------------------
from pymcu.chips import __CHIP__
from pymcu.exceptions import CompileError

if __CHIP__.arch == "avr":
    from pymcu.hal.avr.counter import EdgeCounter
else:
    raise CompileError(
        "counting edges on a pin is not implemented on this architecture yet. It needs the "
        "pin interrupt vectors mapped for the part. Poll the pin with pymcu.hal.gpio and "
        "count the changes yourself, which costs the loop but needs no vector.")
