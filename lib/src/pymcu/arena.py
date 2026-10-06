# -----------------------------------------------------------------------------
# PyMCU arena -- static bump allocator for runtime-sized bytearray(n).
# SPDX-License-Identifier: MIT
# -----------------------------------------------------------------------------
# Backing storage for `bytearray(n)` where n is not a compile-time constant. This is NOT
# user-facing API -- user code never imports this module by name; `pymcu build` injects
# the import when a runtime-sized bytearray(n) is detected in the sources, the same way it
# injects pymcu.strfmt for f-string-as-value assignments. See
# docs/rfcs/0004-arena-allocator.md for the full design and the once rule the compiler
# enforces at every allocation site.
#
# There is no free(). `list[T]` (https://docs.pymcu.org/limitations/#dynamic-memory-and-containers) frees because a list can go
# out of scope inside a loop, and does; a bytearray from alloc() is a permanent buffer for
# the life of the program, so there is nothing to reclaim and no mark/sweep here -- this is
# list[T]'s allocator with the GC removed, which is the whole simplification.
#
# ARENA_SIZE is 0 (arena unused, zero bytes reserved) unless `pymcu build` detects a
# runtime-sized bytearray(n) in the sources, in which case it stages a full replacement of
# THIS FILE into dist/_generated/pymcu/arena.py with ARENA_SIZE's literal changed to the
# real reservation -- the same "generated file shadows the shipped one on the include
# path" mechanism `pymcu build` already uses for board.py (see build.py, the `board_shim`
# block). This is a whole-file substitution rather than a separate imported config module
# on purpose: a cross-module imported constant did not fold as a bytearray() size argument
# when this was tried (see the RFC) even though the same name folds fine in an ordinary
# expression in the same file -- a same-file literal sidesteps that gap entirely.
#
# alloc() returns an OFFSET into _arena, not a raw address, and read8()/write8() are how a
# caller reaches a byte at that offset. This is NOT pointer arithmetic: `ptr(some_array) +
# runtime_offset` does not compute an array's base address on this target -- only a
# constant/register base does (see the RFC) -- so there is no `ptr` here at all. Indexing
# _arena is ordinary variable-indexed fixed-array access, which is what read8()/write8()
# do; the compiler rewrites `buf[i]` / `buf[i] = v` on an arena-allocated name into calls
# to these two, inlined at the call site, so `_arena`'s storage never needs to be named
# from outside this file.
from pymcu.types import uint8, uint16, inline

ARENA_SIZE: uint16 = 0

_arena: bytearray = bytearray(ARENA_SIZE)
_arena_pos: uint16 = 0

# High-water mark: the most bytes ever in use at once. Equal to _arena_pos, since nothing
# is freed, but kept as its own global (rather than reusing _arena_pos for both roles) so
# its name says what it is for anyone reading a .lst/.map, and so an emulator test can
# read "the high-water mark" without having to know it happens to alias the bump pointer.
arena_high_water: uint16 = 0


@inline
def alloc(n: uint16) -> uint16:
    global _arena_pos, arena_high_water
    if _arena_pos + n > ARENA_SIZE:
        raise MemoryError
    base: uint16 = _arena_pos
    _arena_pos = _arena_pos + n
    arena_high_water = _arena_pos
    return base


@inline
def read8(off: uint16) -> uint8:
    return _arena[off]


@inline
def write8(off: uint16, v: uint8) -> None:
    _arena[off] = v


@inline
def high_water() -> uint16:
    # A plain module-level global cannot be read as `module.name` from outside the
    # module today (a separate, pre-existing gap -- module member access resolves
    # functions, not data). arena_high_water itself stays the named global the RFC
    # asks for, readable directly from a .lst/.map or a debugger; this getter is only
    # so an integration test can read it the same way it reads anything else here.
    return arena_high_water
