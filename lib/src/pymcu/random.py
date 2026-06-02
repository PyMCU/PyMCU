# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
#
# random -- pseudo-random number generation for embedded use
#
#   randomSeed(seed)  -- seed the PRNG with a uint32 value
#   random(n)         -- return a pseudo-random uint16 in [0, n)
#   random2(lo, hi)   -- return a pseudo-random uint16 in [lo, hi)
#
# Uses a 32-bit LCG (Knuth / Numerical Recipes):
#
#   state = state * 1664525 + 1013904223   (mod 2^32)
#
# This matches the generator used by avr-libc's rand() function, giving
# familiar output for developers coming from C on AVR.
#
# Limitations:
#   - Period is 2^32 (~4 billion).  Good enough for embedded applications.
#   - Not cryptographically secure.
#   - Seed with an analog noise source (ADC on floating pin) for best results.

from pymcu.types import uint8, uint16, uint32, inline

_state: uint32 = 1


def randomSeed(seed: uint32):
    """Seed the PRNG.  Call once at startup, ideally with analogRead() on a
    floating pin to get a hardware noise-based seed."""
    global _state
    _state = seed


def random(n: uint16) -> uint16:
    """Return a pseudo-random integer in [0, n).  n must be > 0."""
    global _state
    _state = _state * 1664525 + 1013904223
    return (_state >> 16) % n


def random2(lo: uint16, hi: uint16) -> uint16:
    """Return a pseudo-random integer in [lo, hi).  hi must be > lo."""
    return lo + random(hi - lo)
