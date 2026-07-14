# -----------------------------------------------------------------------------
# PyMCU strfmt -- internal f-string-to-buffer formatters.
# SPDX-License-Identifier: MIT
# -----------------------------------------------------------------------------
# Lowering target for f-strings used as VALUES (`s = f"t={t} C"`): the compiler
# expands such an assignment into a fixed bytearray plus chained calls to these
# helpers, threading the write position. This is NOT user-facing API -- user
# code never imports this module; `pymcu build` injects it when an f-string
# value assignment is detected in the sources. No heap, no GC: the buffer is
# a compiler-managed fixed array sized by the f-string's static bound.
#
# Digit extraction branches on the base (shifts/masks for 2/8/16, //10 for
# decimal) so no runtime-base division is needed -- portable to every backend.
from pymcu.types import uint8, uint16, uint32, int32


def _fs_text(buf: bytearray, pos: uint16, s: const[str]) -> uint16:
    # Copy a NUL-terminated string into buf at pos; return the new position.
    i: uint16 = 0
    while True:
        b: uint8 = s[i]
        if b == 0:
            break
        buf[pos] = b
        pos = pos + 1
        i = i + 1
    return pos


def _fs_u32(buf: bytearray, pos: uint16, v: uint32) -> uint16:
    # Decimal digits of v into buf at pos; return the new position.
    if v == 0:
        buf[pos] = 48
        return pos + 1
    digits: uint16 = 0
    n: uint32 = v
    while n > 0:
        digits = digits + 1
        n = n // 10
    i: uint16 = digits
    n = v
    while n > 0:
        i = i - 1
        buf[pos + i] = 48 + uint8(n % 10)
        n = n // 10
    return pos + digits


def _fs_i32(buf: bytearray, pos: uint16, v: int32) -> uint16:
    # Signed decimal (leading '-' when negative); return the new position.
    if v < 0:
        buf[pos] = 45
        return _fs_u32(buf, pos + 1, uint32(0 - v))
    return _fs_u32(buf, pos, uint32(v))


def _fs_fmt(buf: bytearray, pos: uint16, value: int32, base: uint8, width: uint8, flags: uint8) -> uint16:
    # Format-spec path ({x:02x}, {x:b}, {x:5d}...) -- mirrors uart_write_fmt:
    # right-justified to at least `width` chars in `base` (2/8/10/16); `flags`
    # bit 0 = upper-case hex, bit 1 = signed, bit 2 = zero-pad.
    zero_pad: uint8 = flags & 0x04
    pad: uint8 = 32          # ' '
    if zero_pad != 0:
        pad = 48             # '0'
    neg: uint8 = 0
    mag: uint32 = 0
    if (flags & 0x02) and value < 0:
        neg = 1
        mag = uint32(0 - value)
    else:
        mag = uint32(value)
    # Zero-padded negatives put the sign first, then zeros fill the field.
    if neg != 0 and zero_pad != 0:
        buf[pos] = 45        # '-'
        pos = pos + 1
        if width > 0:
            width = width - 1
    tmp: uint8[32] = [0] * 32
    n: uint8 = 0
    if mag == 0:
        tmp[0] = 48
        n = 1
    elif base == 10:
        while mag > 0:
            tmp[n] = 48 + uint8(mag % 10)
            mag = mag // 10
            n = n + 1
    else:
        shift: uint8 = 4
        mask: uint32 = 15
        if base == 2:
            shift = 1
            mask = 1
        elif base == 8:
            shift = 3
            mask = 7
        while mag > 0:
            d: uint8 = uint8(mag & mask)
            if d < 10:
                tmp[n] = d + 48        # '0'..'9'
            elif flags & 0x01:
                tmp[n] = d - 10 + 65   # 'A'..'F'
            else:
                tmp[n] = d - 10 + 97   # 'a'..'f'
            mag = mag >> shift
            n = n + 1
    # Space-padded negatives keep the sign next to the digits, inside the field.
    if neg != 0 and zero_pad == 0:
        tmp[n] = 45
        n = n + 1
    while n < width and n < 32:
        tmp[n] = pad
        n = n + 1
    while n > 0:
        n = n - 1
        buf[pos] = tmp[n]
        pos = pos + 1
    return pos
