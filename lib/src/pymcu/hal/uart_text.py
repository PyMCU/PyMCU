# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
#
# Turning numbers into characters, once, for every architecture.
#
# Nothing here touches a register: every writer is arithmetic over uart_write,
# the one primitive each HAL provides. They lived copied into five files and had
# drifted into three different versions of the same function, which is how
# print_float came to show 1234.5 as "<34.5" on the RP2040 while the ATmega328P
# printed it correctly. One definition cannot disagree with itself.
#
# The dispatch below only resolves uart_write, and it reaches the chip modules
# directly rather than through each architecture's facade. Going through the
# facade would close a cycle: the facade imports these writers, so it cannot
# also be what supplies the primitive they are built on.
# -----------------------------------------------------------------------------
from pymcu.chips import __CHIP__
from pymcu.exceptions import CompileError
from pymcu.types import uint8, uint16, int16, uint32, int32, const

if __CHIP__.name == "attiny2313" or __CHIP__.name == "attiny4313":
    from pymcu.hal.avr.uart.attiny2313 import uart_write
elif __CHIP__.name == "atmega32u4":
    from pymcu.hal.avr.uart.atmega32u4 import uart_write
elif __CHIP__.arch == "avr":
    from pymcu.hal.avr.uart.avr import uart_write
elif __CHIP__.name == "pic16f628a":
    from pymcu.hal.pic14.pic16f628a_uart import uart_write
elif __CHIP__.name == "pic16f877a":
    from pymcu.hal.pic14.pic16f877a_uart import uart_write
elif __CHIP__.name == "pic16f18877":
    from pymcu.hal.pic14.pic16f18877_uart import uart_write
elif __CHIP__.arch == "pic14":
    # A PIC14 with no USART. The writers below are emitted whether or not the program
    # calls them, so this arm binds an inert sink rather than the dispatcher's uart_write,
    # which refuses. Reaching the chip module directly is also what the note at the top of
    # this file says to do; the pic14 arm used to go through the facade instead.
    from pymcu.hal.pic14.pic14_uart_unsupported import uart_write_text_sink as uart_write
elif __CHIP__.arch == "pic18":
    from pymcu.hal.pic18.pic18_uart import uart_write
elif __CHIP__.name == "rp2040" or __CHIP__.name == "rp2350":
    from pymcu.hal.rp.console import uart_write
else:
    raise CompileError("this architecture has no uart_write to build text on")


def uart_write_str(s: const[str]):
    # Not @inline on purpose: as a real subroutine the compiler passes the
    # string by reference and this loop is emitted once, shared by every
    # write_str and println call site.
    i: uint8 = 0
    b: uint8 = s[0]
    while b != 0:
        uart_write(b)
        i = i + 1
        b = s[i]


def uart_write_decimal_u8(value: uint8):
    started: uint8 = 0
    if value >= 100:
        c: uint8 = 48
        while value >= 100:
            value -= 100
            c += 1
        uart_write(c)
        started = 1
    if value >= 10 or started == 1:
        c2: uint8 = 48
        while value >= 10:
            value -= 10
            c2 += 1
        uart_write(c2)
    uart_write(value + 48)


def uart_write_decimal_u16(value: uint16):
    started: uint8 = 0
    for d in [10000, 1000, 100, 10]:
        c: uint8 = 48
        while value >= d:
            value -= d
            c += 1
        if c != 48 or started == 1:
            uart_write(c)
            started = 1
    uart_write(uint8(value) + 48)


def uart_write_decimal_i16(value: int16):
    if value < 0:
        uart_write(45)
        abs_val: uint16 = uint16(0 - value)
        uart_write_decimal_u16(abs_val)
    else:
        uart_write_decimal_u16(uint16(value))


def uart_write_decimal_u32(value: uint32):
    # The divisor walks down by ten instead of coming from a literal list. A
    # `for` over a list of constants is a compile-time sequence, so the body was
    # emitted once per divisor: nine copies of the same 56-byte digit loop, and
    # the routine came to 1014 bytes on an ATmega328P -- 24% of a program whose
    # only job was to print one distance (PyMCU/pymcu-avr#24). Written as a real
    # loop it is emitted once, for the price of one 32-bit division per digit.
    #
    # Still repeated subtraction rather than divmod per digit: dividing `value`
    # as well would need the quotient AND the remainder of the same division at
    # every step, which no backend here fuses into one pass yet, so it costs two
    # library calls a digit instead of one.
    d: uint32 = 1000000000
    started: uint8 = 0
    while d >= 10:
        c: uint8 = 48
        while value >= d:
            value -= d
            c += 1
        if c != 48 or started == 1:
            uart_write(c)
            started = 1
        d = d // 10
    uart_write(uint8(value) + 48)


def uart_write_decimal_i32(value: int32):
    if value < 0:
        uart_write(45)
        abs_val: uint32 = uint32(0 - value)
        uart_write_decimal_u32(abs_val)
    else:
        uart_write_decimal_u32(uint32(value))


def uart_write_hex(value: int32, flags: uint8):
    # Minimal hex for the exception report's `{x:x}` pieces: one nibble at a
    # time off the top of the word, no field width and no leading zeros --
    # the same text format(60, 'x') gives. uart_write_fmt covers the padded
    # forms already, but it carries the generic radix loop and the 32-bit
    # division helpers with it (~900 bytes on AVR); printing one address in
    # an unhandled-exception line is not worth that. flags packs bit0 =
    # upper-case digits, bit1 = the source type is signed (a negative value
    # prints '-' then its magnitude, matching CPython).
    digit_base: uint8 = 87              # 'a' - 10
    if flags & 1:
        digit_base = 55                 # 'A' - 10
    v: uint32 = uint32(value)
    if (flags & 2) and uint8(value >> 24) >= 128:
        uart_write(45)                  # '-'
        v = uint32(0 - value)
    started: uint8 = 0
    shift: uint8 = 28
    while True:
        digit: uint8 = uint8(v >> shift) & 15
        if digit != 0 or started != 0 or shift == 0:
            started = 1
            if digit < 10:
                digit = digit + 48
            else:
                digit = digit + digit_base
            uart_write(digit)
        if shift == 0:
            break
        shift = shift - 4


def uart_write_float(value: float):
    # Two decimals, rounded, on every architecture. The one-decimal truncating
    # variants that used to live in three of these files disagreed with this one
    # about what print_float means, and overflowed their accumulator past 6553.5.
    #
    # The integer part is taken straight from the value, never from a scaled
    # accumulator. Scaling the whole value by 100 first caps what can be printed
    # at 2**32 / 100, so every float past 42949672.95 came out as the same
    # saturated number -- 1e8 and 1e9 both printed 21474836.48. That is the
    # cliff uart_write_float_compact already exists to avoid one width down.
    # Only the fraction is scaled, and it is below 1.0 by construction.
    if value < 0.0:
        uart_write(45)
        # `-value`, not `0.0 - value`: negation is the sign bit, while the
        # subtraction is a call into the soft-float library on the parts that
        # have no FPU. Both give the same answer for every value that reaches
        # here, which is every value strictly below zero.
        value = -value
    int_part: uint32 = uint32(value)
    frac: uint8 = uint8((value - float(int_part)) * 100.0 + 0.5)
    if frac >= 100:
        # The rounding carried out of the fraction: 0.999 is 1.00, not 0.100.
        frac = 0
        int_part += 1
    uart_write_decimal_u32(int_part)
    uart_write(46)
    # frac is below 100 by construction, so the tens digit is at most nine
    # subtractions away. Asking for `//` and `%` instead pulled the whole 8-bit
    # division runtime into the image: 102 bytes on an ATmega328P, for one pair
    # of digits that never leaves two figures.
    tens: uint8 = 48
    while frac >= 10:
        frac -= 10
        tens += 1
    uart_write(tens)
    if frac != 0:
        uart_write(frac + 48)


def _float_fmt_digits(value: float, prec: uint8, digs: bytearray) -> uint32:
    # The first `prec` decimal digits of the fractional part of `value`, plus
    # one rounding digit, written into digs[0..prec]. Returns the integer part
    # with the rounding carry already applied. The caller writes the sign.
    #
    # The digits are EXACT: a float32 is a dyadic m * 2^-e, so its decimal
    # expansion is finite and computable in integers. frac * 2^64 is held in
    # the (hi, lo) limb pair and multiplied by ten per digit; a .5 boundary is
    # then a real tie -- the limbs read exactly 0x8000... -- so the rounding
    # digit lands half-to-even on the decimal expansion, as CPython's does.
    int_part: uint32 = uint32(value)
    frac: float = value - float(int_part)
    hi: uint32 = 0
    lo: uint32 = 0
    if frac > 0.0:
        # Normalise to m in [2^23, 2^24): frac = m * 2^-s. Multiplying by two
        # is exact (exponent step, same mantissa), and f in [2^23, 2^24) is an
        # integer, so uint32(f) loses nothing.
        f: float = frac
        s: uint8 = 0
        while f < 8388608.0:
            f = f * 2.0
            s = s + 1
        m: uint32 = uint32(f)
        # frac * 2^64 = m << (64 - s), split into two 32-bit limbs. m < 2^s
        # because frac < 1, so no shift here can carry out of a limb.
        if s <= 32:
            hi = m << (32 - s)
        elif s <= 64:
            sh: uint8 = s - 32
            rem: uint32 = m
            if sh < 32:
                hi = m >> sh
                rem = m - (hi << sh)
            else:
                hi = 0
            lo = rem << (64 - s)
        elif s <= 88:
            lo = m >> (s - 64)
        # s > 88 leaves hi:lo at zero -- frac < 2^-64, and every digit this
        # routine is asked for (prec <= 15 plus the rounding digit) is zero.
    l0: uint32 = 0
    l1: uint32 = 0
    h0: uint32 = 0
    h1: uint32 = 0
    k: uint8 = 0
    ndig: uint8 = prec + 1
    while k < ndig:
        # (hi:lo) *= 10 a 16-bit limb at a time; the carry out is the digit.
        l0 = (lo & 65535) * 10
        l1 = (lo >> 16) * 10 + (l0 >> 16)
        lo = ((l1 & 65535) << 16) | (l0 & 65535)
        h0 = (hi & 65535) * 10 + (l1 >> 16)
        h1 = (hi >> 16) * 10 + (h0 >> 16)
        hi = ((h1 & 65535) << 16) | (h0 & 65535)
        digs[k] = uint8(h1 >> 16)
        k = k + 1
    rd: uint8 = digs[prec]
    sticky: uint8 = 0
    if (hi | lo) != 0:
        sticky = 1
    roundup: uint8 = 0
    if rd > 5 or (rd == 5 and sticky != 0):
        roundup = 1
    if rd == 5 and sticky == 0:
        # Exact tie: round half to even.
        if prec > 0:
            if (digs[prec - 1] & 1) != 0:
                roundup = 1
        else:
            if (int_part & 1) != 0:
                roundup = 1
    if roundup != 0:
        carry: uint8 = 1
        i: int16 = int16(prec) - 1
        while i >= 0 and carry != 0:
            if digs[i] == 9:
                digs[i] = 0
            else:
                digs[i] = digs[i] + 1
                carry = 0
            i = i - 1
        if carry != 0:
            int_part = int_part + 1
    return int_part


def uart_write_float_fmt(value: float, prec: uint8, width: uint8, flags: uint8):
    # f-string `{v:W.Nf}`: N digits after the decimal point, right-justified to
    # at least W characters, flags bit0 = zero-pad. The digits come from
    # _float_fmt_digits, so the rounding is CPython's: half-to-even on the
    # exact decimal expansion of the float32 value.
    neg: uint8 = 0
    if value < 0.0:
        neg = 1
        value = -value
    digs: uint8[17] = [0] * 17
    int_part: uint32 = _float_fmt_digits(value, prec, digs)
    # The integer part's digit count decides the padding, which has to be
    # known before any of it is written.
    ndig: uint8 = 1
    t: uint32 = int_part
    while t >= 10:
        t = t // 10
        ndig = ndig + 1
    total: uint8 = ndig + neg
    if prec > 0:
        total = total + 1 + prec
    padn: uint8 = 0
    if width > total:
        padn = width - total
    if (flags & 0x01) != 0:
        # Zero-pad: the sign leads, then zeros, then the digits ('-001.2').
        if neg != 0:
            uart_write(45)
        while padn > 0:
            uart_write(48)
            padn = padn - 1
    else:
        while padn > 0:
            uart_write(32)
            padn = padn - 1
        if neg != 0:
            uart_write(45)
    uart_write_decimal_u32(int_part)
    if prec > 0:
        uart_write(46)
        j: uint8 = 0
        while j < prec:
            uart_write(digs[j] + 48)
            j = j + 1


def uart_write_float_compact(value: float):
    # One decimal, for parts where the standard writer does not fit: an
    # ATtiny2313 has 2 KB of flash and uart_write_float pulls in the whole
    # uint32 path with it. Deliberately a different name -- a chip that cannot
    # afford the standard writer says so at the call site instead of quietly
    # printing something else under the same one.
    #
    # The integer part is taken straight from the value rather than from a
    # scaled accumulator. Scaling first is what gave the old per-HAL copies
    # their cliff: a uint16 of tenths wraps at 6553.5 and starts emitting
    # punctuation, which is the defect this rewrite exists to remove, not to
    # rename.
    if value < 0.0:
        uart_write(45)
        value = -value
    int_part: uint16 = uint16(value)
    uart_write_decimal_u16(int_part)
    uart_write(46)
    frac: uint8 = uint8((value - float(int_part)) * 10.0)
    uart_write(frac + 48)
