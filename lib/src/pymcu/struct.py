# SPDX-License-Identifier: MIT
#
# The subset of CPython's `struct` that an ahead-of-time target can do without a heap.
#
# WHAT IS HERE AND WHY THE BODIES ARE EMPTY
#
# These four names exist so `import struct` resolves through the same stdlib-alias fallback
# that already serves `math`, `time` and `random`. The compiler expands every call to them at
# the call site, from the format string read at compile time, so no body ever runs and no
# format is ever parsed on the chip.
#
# THE SUPPORTED SUBSET, which is the shape driver libraries actually write:
#
#     calcsize(fmt)                     folds to a constant
#     unpack(fmt, buf)                  bound to a name: a compile-time sequence of the
#                                       fields, one typed slot each -- index it, slice it,
#                                       iterate it, feed it to list() or a comprehension
#     unpack_from(fmt, buf)[k]          the same, starting at an optional literal offset;
#                                       also indexed ON THE SPOT, one scalar out
#     pack_into(fmt, buf, off, v)       one scalar into a buffer you already own
#     pack(fmt, v...)                   bound to a name: a fixed bytearray of calcsize
#                                       bytes, or a slice-assign source
#                                       (`buf[off:] = struct.pack(fmt, v)`)
#
#     type codes    B b H h I i L l
#     byte order    '<' little, '>' big; no prefix only for a single one-byte field,
#                   because native alignment is not something this can invent
#
# WHAT IS NOT HERE, deliberately: a result used without being bound (the tuple CPython
# builds has no heap to live on, so a bare unpack(...) in a value position is refused),
# `pack_into(..., *values)`, and any format code outside B b H h I i L l. Every one of
# them is refused at the call with a message naming which it was -- never a
# plausible-looking wrong answer.


def calcsize(fmt):
    """Size in bytes of `fmt`. Folded at compile time."""


def unpack(fmt, buf):
    """Every field out of `buf`, as a fixed compile-time sequence. Bind it to a name."""


def unpack_from(fmt, buf, offset=0):
    """The fields out of `buf` at `offset`. Bind it to a name, or index on the spot:
    unpack_from(...)[k]."""


def pack_into(fmt, buf, offset, value):
    """Write `value` into `buf` at `offset`, in the layout `fmt` describes."""


def pack(fmt, *values):
    """A fixed bytearray of calcsize(fmt) bytes holding `values` in fmt's layout.
    Bind it to a name, or use it as a slice-assign source: `buf[off:] = pack(...)`."""
