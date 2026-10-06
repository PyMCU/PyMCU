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


def _f32_pow5(n: int16) -> float:
    # 5**n for any-sign integer n, float32 throughout: repeated multiplication
    # by 5.0 (or its reciprocal), no libm, no bignum. This is the float-only
    # half of MicroPython's mp_decimal_exp trick (10**n = 2**n * 5**n, and the
    # 2**n part is free -- it is just the IEEE-754 exponent field).
    neg: uint8 = 0
    m: int16 = n
    if m < 0:
        neg = 1
        m = 0 - m
    result: float = 1.0
    i: int16 = 0
    while i < m:
        result = result * 5.0
        i = i + 1
    if neg != 0:
        result = 1.0 / result
    return result


def _f32_decimal_exp(num: float, dec_exp: int16) -> float:
    # num * 10**dec_exp, MicroPython's py/parsenum.c:mp_decimal_exp (APPROX
    # mode: mp_float_t IS mp_large_float_t, no wider intermediate). Splits
    # 10**dec_exp = 2**dec_exp * 5**dec_exp: the 2**dec_exp part is added
    # straight into num's IEEE-754 exponent field (exact, free), and only the
    # 5**dec_exp part costs a real multiply or divide. This is the single
    # scaling primitive both the digit search below and its round-trip check
    # call use -- format and verify share one source of rounding, same as
    # MicroPython's own formatter and parser do (both call mp_decimal_exp).
    if dec_exp == 0 or num == 0.0:
        return num
    b: uint32 = bitcast(uint32, num)
    exp: int16 = int16((b >> 23) & 255) + dec_exp
    b2: uint32 = (b & 2155872255) | (uint32(exp) & 255) << 23
    res: float = bitcast(float, b2)
    if dec_exp < 0 and dec_exp >= -10:
        res = res / _f32_pow5(0 - dec_exp)
    else:
        res = res * _f32_pow5(dec_exp)
    return res


def _f32_order(f: float) -> int16:
    # The decimal exponent e of f (positive, finite, nonzero): the smallest e
    # with 10**e <= f < 10**(e+1). Estimated from the binary exponent
    # (log10(2) ~ 0.30103), then corrected by probing the boundary --
    # mp_format_float's own approach.
    b: uint32 = bitcast(uint32, f)
    be: int16 = int16((b >> 23) & 255) - 127
    # log10(2) ~ 77/256, integer estimate -- avoids a float multiply/cast at
    # a call site that already pays for two more of them right below. Only
    # needs to be CLOSE: the probe loop below corrects it either way.
    e: int16 = (be * 77) >> 8
    positive: uint8 = 0
    if b >= 1065353216:
        positive = 1
    pe: int16 = int16(positive)
    u_base: float = _f32_decimal_exp(1.0, e + pe)
    while True:
        ge: uint8 = 0
        if f >= u_base:
            ge = 1
        if ge != positive:
            break
        if positive != 0:
            e = e + 1
        else:
            e = e - 1
        u_base = _f32_decimal_exp(1.0, e + pe)
    return e


def _f32_repr(value: float, out: bytearray) -> uint8:
    # print(value)/str(value)/repr(value) for an IEEE-754 binary32, following
    # MicroPython's ACTUAL float32 print algorithm (py/formatfloat.c's
    # mp_format_float, fmt='g', prec=MP_FLOAT_REPR_PREC -- the float-format
    # path every float32 MicroPython/CircuitPython port builds with by
    # default) -- not CPython's shortest-round-trip repr, and not an exact
    # decimal expansion: MicroPython's own reference scales by powers of ten
    # using FLOAT arithmetic (mp_decimal_exp, no bignum, no libm pow), so its
    # last significant digit is not always exact, and this routine matches
    # that on purpose, not despite it.
    #
    # This is a SIMPLIFIED port of mp_format_float's digit search: the real
    # C algorithm computes an initial estimate at reduced precision (to keep
    # the intermediate float32 multiply/divide accurate) then runs a
    # dichotomic bisection to correct the last 1-2 digits against a round-
    # trip check. That bisection measured larger than the entire ~2 KB
    # budget by itself. This version drops it: one _f32_decimal_exp call
    # gives the num_digits-digit estimate directly, and if it does not
    # round-trip exactly, the search tries ONE MORE significant digit
    # (6 up to 9) rather than searching nearby mantissa values. Measured
    # against a real MicroPython float32 build (py/formatfloat.c, unix port,
    # MICROPY_FLOAT_IMPL_FLOAT): every representative/sensor-range value
    # tested matches exactly; ~11% of uniformly random float32 bit patterns
    # in the same magnitude range differ in the last digit (see
    # https://docs.pymcu.org/limitations/#float-text) -- always by a tiny amount, and always
    # because this version needed one more digit than MicroPython's
    # corrected search did to reach the same round-trip guarantee.
    bits: uint32 = bitcast(uint32, value)
    sign: uint32 = bits >> 31
    e: int16 = int16((bits >> 23) & 255)
    mant: uint32 = bits & 8388607
    pos: uint8 = 0
    if e == 255:
        if mant != 0:
            out[0] = 110
            out[1] = 97
            out[2] = 110          # 'nan' -- CPython drops a nan's sign bit
            return 3
        if sign != 0:
            out[0] = 45
            pos = 1
        out[pos] = 105
        out[pos + 1] = 110
        out[pos + 2] = 102        # 'inf'
        return pos + 3
    if e == 0 and mant == 0:
        if sign != 0:
            out[0] = 45
            pos = 1
        out[pos] = 48
        out[pos + 1] = 46
        out[pos + 2] = 48         # '0.0' / '-0.0'
        return pos + 3
    if sign != 0:
        out[0] = 45
        pos = 1
    f: float = value
    if sign != 0:
        f = -f
    ordv: int16 = _f32_order(f)
    num_digits: uint8 = 6
    cap: uint32 = 1000000              # 10**6, kept in step with num_digits by *10 below
    xf: uint32 = 0
    xw: int16 = 0
    while True:
        decexp: int16 = int16(num_digits) - ordv - 1
        mf: float = _f32_decimal_exp(f, decexp)
        m: uint32 = uint32(mf + 0.5)
        decexp2: int16 = decexp
        if m >= cap:
            m = m // 10
            decexp2 = decexp - 1
        check: float = _f32_decimal_exp(float(m), 0 - decexp2)
        if check == f or num_digits >= 9:
            xf = m
            xw = 0 - decexp2
            break
        num_digits = num_digits + 1
        cap = cap * 10
    # xd gets X's digits least significant first; trailing zeros (the head of
    # xd) drop, the significant count is nd, and the decimal point sits nd
    # digits before X * 10**xw itself.
    xd: uint8[10] = [0] * 10
    nd: int8 = 0
    xt: uint32 = xf
    while xt > 0:
        xd[nd] = uint8(xt % 10)
        xt = xt // 10
        nd = nd + 1
    decpt: int8 = int8(xw) + nd
    while nd > 1 and xd[0] == 0:
        zi: int8 = 0
        while zi < nd - 1:
            xd[zi] = xd[zi + 1]
            zi = zi + 1
        nd = nd - 1
    if decpt <= -4 or decpt > 16:
        # d[.ddd]e+NN -- the exponent is always two digits on float32's range.
        out[pos] = xd[nd - 1] + 48
        pos = pos + 1
        if nd > 1:
            out[pos] = 46
            pos = pos + 1
            i3: int8 = 1
            while i3 < nd:
                out[pos] = xd[nd - 1 - i3] + 48
                pos = pos + 1
                i3 = i3 + 1
        out[pos] = 101
        pos = pos + 1
        ex: int8 = decpt - 1
        if ex < 0:
            out[pos] = 45
            pos = pos + 1
            ex = 0 - ex
        else:
            out[pos] = 43
            pos = pos + 1
        out[pos] = uint8(ex // 10) + 48
        pos = pos + 1
        out[pos] = uint8(ex % 10) + 48
        pos = pos + 1
    elif decpt <= 0:
        # 0.00ddd -- leading zeros between the point and the digits.
        out[pos] = 48
        pos = pos + 1
        out[pos] = 46
        pos = pos + 1
        i4: int8 = decpt
        while i4 < 0:
            out[pos] = 48
            pos = pos + 1
            i4 = i4 + 1
        i4 = 0
        while i4 < nd:
            out[pos] = xd[nd - 1 - i4] + 48
            pos = pos + 1
            i4 = i4 + 1
    elif nd <= decpt:
        # ddd000.0 -- the digits, zero-padded out to the point, then '.0'.
        i5: int8 = 0
        while i5 < nd:
            out[pos] = xd[nd - 1 - i5] + 48
            pos = pos + 1
            i5 = i5 + 1
        i5 = nd
        while i5 < decpt:
            out[pos] = 48
            pos = pos + 1
            i5 = i5 + 1
        out[pos] = 46
        pos = pos + 1
        out[pos] = 48
        pos = pos + 1
    else:
        # dd.ddd -- the point lands inside the digits.
        i6: int8 = 0
        while i6 < decpt:
            out[pos] = xd[nd - 1 - i6] + 48
            pos = pos + 1
            i6 = i6 + 1
        out[pos] = 46
        pos = pos + 1
        while i6 < nd:
            out[pos] = xd[nd - 1 - i6] + 48
            pos = pos + 1
            i6 = i6 + 1
    return pos


def uart_write_float(value: float):
    # MicroPython's 7-significant-digit float policy (see _f32_repr).
    # The character hopper is cheap insurance: the longest answer is
    # "-340282346638528859811704183484516925440.0"-shaped fixed notation at
    # decpt 16, which is 19 bytes.
    out: uint8[20] = [0] * 20
    n: uint8 = _f32_repr(value, out)
    i: uint8 = 0
    while i < n:
        uart_write(out[i])
        i = i + 1


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
