# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
#
# array -- CPython's array module, read as the heap-bounded list[T] the compiler
# already has (PyMCU#<issue>).
#
# array.array(typecode) is recognized specially by the compiler wherever bytearray()
# and list() already are: an empty call maps onto list[T]() with T decided by the
# typecode (B/b uint8/int8, H/h uint16/int16, I/L/i/l uint32/int32; f/d/q are refused,
# there is no float/double/int64 element width here). array.array(typecode, [...])
# with a compile-time list is the same shape a list[T] literal already is.
#
# This class exists only so `import array` resolves to a real module and
# `array.array` names something -- the compiler never actually constructs an
# instance of it. There is no body to give array.array() the way there is none
# for bytearray() or list(); both are recognized by their call shape before
# reaching an ordinary constructor.
class array:
    pass
