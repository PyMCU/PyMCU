# SPDX-License-Identifier: MIT
#
# The subset of CPython's `struct` that an ahead-of-time target can do without a heap.
#
# WHAT IS HERE AND WHY THE BODIES ARE EMPTY
#
# These three names exist so `import struct` resolves through the same stdlib-alias fallback
# that already serves `math`, `time` and `random`. The compiler expands every call to them at
# the call site, from the format string read at compile time, so no body ever runs and no
# format is ever parsed on the chip.
#
# THE SUPPORTED SUBSET, which is the shape driver libraries actually write:
#
#     calcsize(fmt)                  folds to a constant
#     unpack_from(fmt, buf, off)[k]  literal k, indexed ON THE SPOT, one scalar out
#     pack_into(fmt, buf, off, v)    one scalar into a buffer you already own
#
#     type codes    B b H h
#     byte order    '<' little, '>' big; no prefix only for a single one-byte field,
#                   because native alignment is not something this can invent
#
# WHAT IS NOT HERE, deliberately: a result that escapes as a tuple, iterating a result,
# isinstance/len on one, and `pack_into(..., *values)`. Those need a tuple to exist as a
# value, and there is no heap to hold one. Every one of them is refused at the call with a
# message naming which it was -- never a plausible-looking wrong answer.


def calcsize(fmt):
    """Size in bytes of `fmt`. Folded at compile time."""


def unpack_from(fmt, buf, offset=0):
    """One field out of `buf`. Must be indexed on the spot: unpack_from(...)[k]."""


def pack_into(fmt, buf, offset, value):
    """Write `value` into `buf` at `offset`, in the layout `fmt` describes."""
