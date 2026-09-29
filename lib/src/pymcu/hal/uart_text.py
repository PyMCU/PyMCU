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


def _f32_scale(num: uint32, t: int16, w: int16, mode: uint8) -> uint32:
    # Lazy prefix query: floor(num * 2^t / 10^w) without materializing the
    # decimal expansion. num * 2^t / 10^w = num * 2^(t-w) / 5^w for w >= 0 and
    # num * 5^(-w) * 2^(t-w) for w < 0, so the work is a multiply-by-5 chain, a
    # shift, or a divide-by-5 chain on a 16-bit little-endian limb array (the
    # type set has no u64; a limb multiply or divide step stays in a u32).
    # The divide steps must run AFTER the shift so the dividend keeps its low
    # bits, and a right shift can run after a 5-division because the dropped
    # remainder can never carry into the quotient.
    #
    # mode 0 returns the quotient (which fits a u32 on every call the scan
    # makes) with bit 31 set iff the dropped tail was nonzero. mode 1 answers
    # the digit just past a deeper cut: (quotient mod 10) << 1 | tail-nonzero.
    limbs: uint16[16] = [0] * 16
    limbs[0] = uint16(num & 65535)
    limbs[1] = uint16(num >> 16)
    nl: int16 = 2
    remz: uint8 = 0
    s: int16 = t - w
    if w < 0:
        i: int16 = w
        while i < 0:
            c: uint32 = 0
            j: int16 = 0
            while j < nl:
                cur: uint32 = uint32(limbs[j]) * 5 + c
                limbs[j] = uint16(cur & 65535)
                c = cur >> 16
                j = j + 1
            if c != 0:
                limbs[nl] = uint16(c)
                nl = nl + 1
            i = i + 1
    if s > 0:
        bs: int16 = s & 15
        ws: int16 = s >> 4
        if bs != 0:
            c2: uint32 = 0
            j2: int16 = 0
            while j2 < nl:
                cur2: uint32 = (uint32(limbs[j2]) << bs) | c2
                limbs[j2] = uint16(cur2 & 65535)
                c2 = cur2 >> 16
                j2 = j2 + 1
            if c2 != 0:
                limbs[nl] = uint16(c2)
                nl = nl + 1
        while ws > 0:
            j3: int16 = nl
            while j3 > 0:
                limbs[j3] = limbs[j3 - 1]
                j3 = j3 - 1
            limbs[0] = 0
            nl = nl + 1
            ws = ws - 1
    if w > 0:
        iw: int16 = 0
        while iw < w:
            c3: uint32 = 0
            j4: int16 = nl - 1
            while j4 >= 0:
                cur3: uint32 = (c3 << 16) | uint32(limbs[j4])
                limbs[j4] = uint16(cur3 // 5)
                c3 = cur3 % 5
                j4 = j4 - 1
            if c3 != 0:
                remz = 1
            iw = iw + 1
    if s < 0:
        ws2: int16 = (0 - s) >> 4
        bs2: int16 = (0 - s) & 15
        while ws2 > 0 and nl > 0:
            if limbs[0] != 0:
                remz = 1
            j5: int16 = 0
            while j5 < nl - 1:
                limbs[j5] = limbs[j5 + 1]
                j5 = j5 + 1
            nl = nl - 1
            ws2 = ws2 - 1
        if bs2 != 0 and nl > 0:
            j6: int16 = nl - 1
            c4: uint32 = 0
            mask: uint32 = uint32((1 << bs2) - 1)
            while j6 >= 0:
                nc4: uint32 = uint32(limbs[j6]) & mask
                limbs[j6] = uint16((uint32(limbs[j6]) >> bs2) | (c4 << (16 - bs2)))
                c4 = nc4
                j6 = j6 - 1
            if c4 != 0:
                remz = 1
    if mode != 0:
        c5: uint32 = 0
        j7: int16 = nl - 1
        while j7 >= 0:
            cur5: uint32 = (c5 << 16) | uint32(limbs[j7])
            c5 = cur5 % 10
            j7 = j7 - 1
        return uint32((c5 << 1) | uint32(remz))
    q: uint32 = uint32(limbs[0])
    if nl > 1:
        q = q | (uint32(limbs[1]) << 16)
    return q | (uint32(remz) << 31)


def _f32_repr(value: float, out: bytearray) -> uint8:
    # print(value)/str(value)/repr(value) for an IEEE-754 binary32, following
    # MicroPython's float policy (mp_format_float on a single-precision
    # build): exactly 7 significant digits, half-to-even on the value's exact
    # decimal expansion, trailing zeros dropped, laid out the way CPython
    # lays floats out -- fixed notation while the decimal point sits inside
    # -4..16, scientific outside it, 'inf'/'nan' for the non-finite
    # encodings, and the sign kept on zero. out gets the characters; returns
    # the count (<= 19).
    #
    # This is NOT the shortest round-trip repr (Ryu/Grisu/Errol): an earlier
    # version here searched the rounding interval for the shortest digit
    # count that identifies the float32 exactly, which is what CPython's
    # repr does for float64. That routine measured ~4.2 KB of AVR flash on
    # top of _f32_scale's ~2.2 KB, well past the ~2 KB budget for a HAL
    # helper, and a fixed digit count buys back most of it: no lo/hi bound
    # tracking, no scan over candidate digit counts, one _f32_scale call for
    # the digits and one for the rounding decision.
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
    m: uint32 = mant | 8388608
    e2: int16 = e - 150
    if e == 0:
        m = mant
        e2 = -149
    # he10 = the decimal digit count of the value: the smallest he10 with
    # 10^(he10-1) <= v. Estimated from bit length -- log10(2) ~ 77/256 and
    # log2(5) ~ 37/16 -- then corrected by probing the top digit.
    nb: int16 = 0
    hb: uint32 = m
    while hb != 0:
        hb = hb >> 1
        nb = nb + 1
    if e2 >= 0:
        nb = nb + e2
    else:
        nb = nb + (((0 - e2) * 37) >> 4)
    he10: int16 = ((nb * 77) >> 8) + 1
    if e2 < 0:
        he10 = he10 + e2
    while True:
        pk: uint32 = _f32_scale(m, e2, he10 - 1, 0) & 2147483647
        if pk == 0:
            he10 = he10 - 1
        elif pk >= 10:
            he10 = he10 + 1
        else:
            break
    # Exactly 7 significant digits (MicroPython's float32 precision), the
    # last one half-to-even on the value's exact decimal expansion. w is
    # that last digit's weight; xf is the rounded 7-digit prefix. A round-up
    # can carry out to 10**7 (9.9999996 -> 10000000): the digit-extraction
    # below turns that into an 8th digit and decpt absorbs it, so no special
    # case is needed here.
    w: int16 = he10 - 7
    xf: uint32 = _f32_scale(m, e2, w, 0) & 2147483647
    vr: uint32 = _f32_scale(m, e2, w - 1, 1)
    dr: uint32 = vr >> 1
    if dr > 5 or (dr == 5 and ((vr & 1) != 0 or (xf & 1) != 0)):
        xf = xf + 1
    xw: int16 = w
    # xd gets X's digits least significant first; trailing zeros (the head of
    # xd) drop, the significant count is nd, and the decimal point sits nd
    # digits before X * 10**xw itself.
    xd: uint8[10] = [0] * 10
    nd: int32 = 0
    xt: uint32 = xf
    while xt > 0:
        xd[nd] = uint8(xt % 10)
        xt = xt // 10
        nd = nd + 1
    decpt: int32 = xw + nd
    while nd > 1 and xd[0] == 0:
        zi: int32 = 0
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
            i3: int32 = 1
            while i3 < nd:
                out[pos] = xd[nd - 1 - i3] + 48
                pos = pos + 1
                i3 = i3 + 1
        out[pos] = 101
        pos = pos + 1
        ex: int32 = decpt - 1
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
        i4: int32 = decpt
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
        i5: int32 = 0
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
        i6: int32 = 0
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
