# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
#
# Reading a quadrature encoder -- pymcu.hal.encoder
#
#   knob = Quadrature("PD2", "PD3")
#   where = knob.position()       # four counts per detent on a common knob
#   knob.set_position(0)
#
# Two lines a quarter turn out of phase; which one changed first says which way. What does
# the decoding -- a pin interrupt, a hardware quadrature unit, a PIO program -- is the
# chip's business, and a chip that has none of them says so instead of guessing.
# -----------------------------------------------------------------------------
from pymcu.chips import __CHIP__
from pymcu.exceptions import CompileError

if __CHIP__.arch == "avr":
    from pymcu.hal.avr.encoder import Quadrature
else:
    raise CompileError(
        "reading a quadrature encoder is not implemented on this architecture yet. It needs "
        "the pin interrupt vectors mapped for the part. Read the two pins in your loop with "
        "pymcu.hal.gpio and decode them yourself, which costs the loop but needs no vector.")
