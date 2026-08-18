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
elif __CHIP__.arch == "pic14":
    from pymcu.hal.pic14.pic14_uart import uart_write
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
    started: uint8 = 0
    for d in [1000000000, 100000000, 10000000, 1000000, 100000, 10000, 1000, 100, 10]:
        c: uint8 = 48
        while value >= d:
            value -= d
            c += 1
        if c != 48 or started == 1:
            uart_write(c)
            started = 1
    uart_write(uint8(value) + 48)


def uart_write_decimal_i32(value: int32):
    if value < 0:
        uart_write(45)
        abs_val: uint32 = uint32(0 - value)
        uart_write_decimal_u32(abs_val)
    else:
        uart_write_decimal_u32(uint32(value))


def uart_write_float(value: float):
    # Two decimals, rounded, on every architecture. The one-decimal truncating
    # variants that used to live in three of these files disagreed with this one
    # about what print_float means, and overflowed their accumulator past 6553.5.
    if value < 0.0:
        uart_write(45)
        value = 0.0 - value
    centis: uint32 = uint32(value * 100.0 + 0.5)
    int_part: uint32 = centis // 100
    frac: uint8 = uint8(centis % 100)
    uart_write_decimal_u32(int_part)
    uart_write(46)
    d1: uint8 = frac // 10
    d2: uint8 = frac % 10
    uart_write(d1 + 48)
    if d2 != 0:
        uart_write(d2 + 48)


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
        value = 0.0 - value
    int_part: uint16 = uint16(value)
    uart_write_decimal_u16(int_part)
    uart_write(46)
    frac: uint8 = uint8((value - float(int_part)) * 10.0)
    uart_write(frac + 48)
