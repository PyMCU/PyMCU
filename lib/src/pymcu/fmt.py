# -----------------------------------------------------------------------------
# PyMCU fmt -- format values into a caller-provided fixed buffer (no heap, no GC).
# SPDX-License-Identifier: MIT
# -----------------------------------------------------------------------------
# Building strings at runtime needs a buffer, not the heap. These helpers write
# into a `bytearray` the caller owns and return the new write position, so they
# chain: `n = fmt.text(buf, 0, "t="); n = fmt.u32(buf, n, t); n = fmt.text(buf, n, "C")`.
# They are also the lowering target of `format_into(buf, f"...")`.
from pymcu.types import uint32, int32, uint8  # noqa: F401


def u32(buf: bytearray, pos: uint32, v: uint32) -> uint32:
    """Write the decimal digits of `v` into buf at pos; return the new position."""
    if v == 0:
        buf[pos] = 48          # '0'
        return pos + 1
    # Count digits, then emit them most-significant-first by indexing backwards.
    digits: uint32 = 0
    n: uint32 = v
    while n > 0:
        digits = digits + 1
        n = n // 10
    i: uint32 = digits
    n = v
    while n > 0:
        i = i - 1
        buf[pos + i] = 48 + (n % 10)
        n = n // 10
    return pos + digits


def i32(buf: bytearray, pos: uint32, v: int32) -> uint32:
    """Write a signed decimal (with a leading '-' when negative)."""
    if v < 0:
        buf[pos] = 45          # '-'
        return u32(buf, pos + 1, 0 - v)
    return u32(buf, pos, v)


def hex8(buf: bytearray, pos: uint32, v: uint32, width: uint32) -> uint32:
    """Write `v` as zero-padded lowercase hex of `width` nibbles; return new position."""
    i: uint32 = width
    while i > 0:
        i = i - 1
        nib: uint32 = (v >> (i * 4)) & 0xF
        if nib < 10:
            buf[pos] = 48 + nib            # '0'..'9'
        else:
            buf[pos] = 87 + nib            # 'a'..'f' (97 - 10)
        pos = pos + 1
    return pos
