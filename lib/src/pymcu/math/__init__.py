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
#   floor(x) / ceil(x) / trunc(x)               -- a float to the integer below,
#                                                  above, or toward zero

from pymcu.types import uint8, uint16, int16, int32, inline


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


# floor / ceil / trunc.
#
# Each has an integer overload returning its argument. An integer is already whole, so
# `math.floor(count)` costs nothing instead of pulling the software float routines in behind
# the caller's back -- on this target the float library is the largest single thing the stdlib
# can add to an image, so widening an integer to get an integer back is not a free convenience.
#
# The float overloads build on int32(x), which truncates toward zero, and adjust by one only
# when x actually had a fractional part. `_as_float(t) != x` is that test: t is x with the
# fraction removed, so the two differ exactly when there was one.
#
# _as_float exists because writing `float(t)` directly inside an OVERLOADED @inline resolves
# to the integer overload of the enclosing function instead of to the cast, and the build
# fails with "function 'floor' is recursive". A non-overloaded helper is not affected. See
# PyMCU#182; when that is fixed the helper can be inlined back into its two call sites and
# nothing else changes.
#
# PyMCU#182 also means the integer overloads cannot currently be SELECTED: an integer
# argument picks the float form and pays for the software float routines, 476 bytes against
# the 142 the integer path costs when it is the only candidate. They are written here anyway,
# because they are what the code should say and the day #182 is fixed they start working with
# no change to this file.


@inline
def _as_float(v: int32) -> float:
    return float(v)


@inline
def trunc(x: int32) -> int32:
    """The integral part of x. An integer is already integral."""
    return x


@inline
def trunc(x: float) -> int32:
    """The integral part of x, toward zero. trunc(2.7) is 2, trunc(-2.7) is -2."""
    return int32(x)


@inline
def floor(x: int32) -> int32:
    """The largest integer not greater than x. An integer is its own floor."""
    return x


@inline
def floor(x: float) -> int32:
    """The largest integer not greater than x. floor(2.7) is 2, floor(-2.7) is -3."""
    t: int32 = int32(x)
    if x < 0.0:
        if _as_float(t) != x:
            t = t - 1
    return t


@inline
def ceil(x: int32) -> int32:
    """The smallest integer not less than x. An integer is its own ceiling."""
    return x


@inline
def ceil(x: float) -> int32:
    """The smallest integer not less than x. ceil(2.1) is 3, ceil(-2.1) is -2."""
    t: int32 = int32(x)
    if x > 0.0:
        if _as_float(t) != x:
            t = t + 1
    return t
