# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
#
# PIC18 UART dispatcher -- selects the chip implementation at compile time via
# module-level conditional imports (same pattern as the AVR and PIC14 facades).
# -----------------------------------------------------------------------------
from pymcu.chips import __CHIP__
from pymcu.exceptions import CompileError
from pymcu.types import uint8, uint16, int16, uint32, int32, const

if __CHIP__.name == "pic18f45k50":
    from pymcu.hal.pic18.pic18f45k50_uart import (
        uart_init, uart_write, uart_read, uart_read_ready, uart_write_byte,
    )
else:
    raise CompileError("UART is not implemented for this PIC18 chip")


def uart_write_str(s: const[str]):
    # Non-@inline on purpose (same rationale as the AVR and PIC14 HALs): as a
    # real subroutine the compiler passes the string by reference and this loop
    # is emitted once and shared by every write_str/println call site.
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
    if value < 0.0:
        uart_write(45)
        value = 0.0 - value
    centis: uint32 = uint32(value * 100.0 + 0.5)
    int_part: uint16 = uint16(centis // 100)
    frac: uint8 = uint8(centis % 100)
    started: uint8 = 0
    if int_part >= 10000:
        c: uint8 = 48
        while int_part >= 10000:
            int_part -= 10000
            c += 1
        uart_write(c)
        started = 1
    if started or int_part >= 1000:
        c2: uint8 = 48
        while int_part >= 1000:
            int_part -= 1000
            c2 += 1
        uart_write(c2)
        started = 1
    if started or int_part >= 100:
        c3: uint8 = 48
        while int_part >= 100:
            int_part -= 100
            c3 += 1
        uart_write(c3)
        started = 1
    if started or int_part >= 10:
        c4: uint8 = 48
        while int_part >= 10:
            int_part -= 10
            c4 += 1
        uart_write(c4)
    uart_write(uint8(int_part) + 48)
    uart_write(46)
    d1: uint8 = frac // 10
    d2: uint8 = frac % 10
    uart_write(d1 + 48)
    if d2 != 0:
        uart_write(d2 + 48)
