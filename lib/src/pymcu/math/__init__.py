# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
#
# math -- integer utilities for embedded use
#
#   map_range(x, in_lo, in_hi, out_lo, out_hi) -- linear mapping between ranges
#   constrain(x, lo, hi)                        -- clamp a value to [lo, hi]

from pymcu.types import uint8, uint16, int16, inline


@inline
def map_range(x: uint16, in_lo: uint16, in_hi: uint16, out_lo: uint16, out_hi: uint16) -> uint16:
    """Map x from [in_lo, in_hi] to [out_lo, out_hi].

    Equivalent to Arduino's map() function.  Uses integer arithmetic; the
    result is truncated (not rounded).  Safe when in_hi != in_lo.
    """
    return (x - in_lo) * (out_hi - out_lo) // (in_hi - in_lo) + out_lo


@inline
def constrain(x: uint16, lo: uint16, hi: uint16) -> uint16:
    """Clamp x to the closed interval [lo, hi].

    Equivalent to Arduino's constrain() macro.
    """
    if x < lo:
        return lo
    if x > hi:
        return hi
    return x
