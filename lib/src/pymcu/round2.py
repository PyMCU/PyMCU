# -----------------------------------------------------------------------------
# PyMCU round2 -- internal lowering target for round(x, n).
# SPDX-License-Identifier: MIT
# -----------------------------------------------------------------------------
# `round(x)` (one argument) folds at compile time -- see EmitRoundBuiltin. The
# two-argument form needs a decimal DIGIT COUNT, which is a run-time quantity
# reused across every call site of every n, so it is a real subroutine rather
# than a per-n compile-time unroll: `n` is a compile-time constant at every
# PyMCU call site today (P2 AVR gaps bundle, item 2), but this function does
# not require that -- it works for any n it is handed.
#
# This is NOT user-facing API -- user code never imports this module; `pymcu
# build` injects it when round(x, n) is detected in the sources, the same way
# pymcu.strfmt is injected for an f-string value.
#
# round(x, n) shares its digit machinery with the f-string float FORMAT SPEC
# (_float_fmt_digits: half-to-even on the EXACT decimal expansion of the
# float32 value, the same algorithm CPython's round() uses on its own float).
# n >= 0 rounds to n digits after the point; n < 0 rounds to a multiple of
# 10 ** -n (round(1234.0, -2) == 1200.0), by rounding the SCALED value to the
# nearest integer (n's magnitude is capped at 15 -- see EmitRoundBuiltin --
# so both loops below terminate quickly and digs never needs more than 16
# bytes).
from pymcu.types import uint8, uint32, int32
from pymcu.hal.uart_text import _float_fmt_digits


def _pymcu_round2(value: float, n: int32) -> float:
    if n >= 0:
        neg: uint8 = 0
        av: float = value
        # The sign comes off the IEEE-754 sign bit, not a comparison: -0.0
        # fails `av < 0.0` and would lose its minus (round(-0.0, 2) == -0.0).
        if (bitcast(uint32, av) >> 31) != 0:
            neg = 1
            av = -av
        prec: uint8 = uint8(n)
        digs: uint8[16] = [0] * 16
        int_part: uint32 = _float_fmt_digits(av, prec, digs)
        # Rebuild the fraction as 0.d0 d1 ... d(prec-1) in Horner form, last
        # digit first: every step is one correctly-rounded float op, and an
        # error made on an earlier digit is divided by ten at every step that
        # follows it, so the sum stays inside the last ulp even at prec 15.
        # The uint32 accumulator this replaced overflowed past nine digits
        # (round(0.5, 10) read back 0.07050327).
        fracf: float = 0.0
        i: uint8 = prec
        while i > 0:
            i = i - 1
            fracf = (fracf + float(digs[i])) / 10.0
        result: float = float(int_part) + fracf
        if neg != 0:
            result = -result
        return result
    else:
        m: uint8 = uint8(0 - n)
        scale2: float = 1.0
        k: uint8 = 0
        while k < m:
            scale2 = scale2 * 10.0
            k = k + 1
        scaled: float = value / scale2
        neg2: uint8 = 0
        if (bitcast(uint32, scaled) >> 31) != 0:
            neg2 = 1
            scaled = -scaled
        digs0: uint8[1] = [0]
        int_part2: uint32 = _float_fmt_digits(scaled, 0, digs0)
        r: float = float(int_part2)
        if neg2 != 0:
            r = -r
        return r * scale2
