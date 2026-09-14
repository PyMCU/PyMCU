# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
#
# WS2812 emitter -- ATmega48/88/168/328 at 16 MHz
#
# The timing IS the protocol here. There is no clock line, so a one is told from a
# zero by how long the data line stays high. At 16 MHz a cycle is 62.5 ns.
#
#   what    datasheet, 150 ns either way    measured
#   T0H     400 ns                           7 cycles, 437 ns
#   T1H     800 ns                          12 cycles, 750 ns
#   T0L     850 ns                          14 cycles, 875 ns
#   T1L     450 ns                          13 cycles, 812 ns   over, see below
#   reset   more than 50 us                 55 us
#
# ONE FUNCTION PER PIN, and that is the whole point of the file's shape.
#
# It used to be two functions, `_ws2812_b(bit, val)` and `_ws2812_d(bit, val)`, taking
# the bit index as an argument. Being non-inline made that index a runtime value, so
# the `match` turning it into an SBI sat INSIDE the bit loop and all eight bits paid
# for the dispatch again: measured on PD6, 19 of the 41 cycles a bit cost were four
# failed comparisons and the one that matched. A bit is allowed 20 cycles in total.
#
# With the pin in the function's name there is nothing left to dispatch on. The match
# stays where it belongs, in the @inline `ws2812_write_byte`, where it folds, and only
# the function a program actually names is emitted.
#
# None of these is @inline. They hold the only loops in the file, and a label inside an
# inlined body is emitted once per call site, which the assembler rejects.
#
# `asm("RJMP .+0")` is two cycles in one word, where two NOPs are two cycles in two.
# The long pad on the one path is written that way because it is repeated twelve times
# and the bytes show: it is what keeps this file smaller than the dispatch it replaced.
# The short pads stay NOPs, where a cycle and a word are the same thing.
#
# STILL SLOW, by a quarter, and only on the ones. A zero costs 21 cycles, inside the
# 1.25 us a bit is allowed; a one costs 25, which is 11 percent over, and its low is
# 812 ns where the datasheet says 450. The twelve cycles a rolled loop spends between
# the edges are not this file's to spend: the while head reloads the counter through
# R24, the shift is MOV/LSL/MOV where a bare LSL would do, and `b >= 128` is
# MOV/CPI/branch where a shift into carry would be one instruction and would do the
# shift as well. A strip reads the frame either way, since what ends one is the line
# staying low past 50 us and the longest gap here is 2.7 us. PyMCU#355.
#
# Interrupts have to be off for the duration: one taken mid-byte stretches a high time
# past its tolerance and that pixel latches the wrong colour. The caller owns that,
# because the caller is what knows how long the frame is.
# -----------------------------------------------------------------------------
from pymcu.exceptions import CompileError
from pymcu.types import uint8, uint16, inline, ptr, asm
from pymcu.chips.atmega328p import PORTB, PORTD, DDRB, DDRD
from pymcu.time import delay_us


_REFUSAL = "NeoPixel: unsupported data pin -- use PB0-PB5 or PD2-PD7"


@inline
def ws2812_init(pin: str):
    # Drive the data pin and hold it low. A line left high reads as the front of a bit.
    match pin:
        case "PB0":
            DDRB[0] = 1
            PORTB[0] = 0
        case "PB1":
            DDRB[1] = 1
            PORTB[1] = 0
        case "PB2":
            DDRB[2] = 1
            PORTB[2] = 0
        case "PB3":
            DDRB[3] = 1
            PORTB[3] = 0
        case "PB4":
            DDRB[4] = 1
            PORTB[4] = 0
        case "PB5":
            DDRB[5] = 1
            PORTB[5] = 0
        case "PD2":
            DDRD[2] = 1
            PORTD[2] = 0
        case "PD3":
            DDRD[3] = 1
            PORTD[3] = 0
        case "PD4":
            DDRD[4] = 1
            PORTD[4] = 0
        case "PD5":
            DDRD[5] = 1
            PORTD[5] = 0
        case "PD6":
            DDRD[6] = 1
            PORTD[6] = 0
        case "PD7":
            DDRD[7] = 1
            PORTD[7] = 0
        case _:
            raise CompileError(_REFUSAL)


@inline
def ws2812_write_byte(pin: str, val: uint8):
    # The pin is a compile-time name, so every arm but one folds away and what is left
    # is a single CALL into a single tight loop.
    match pin:
        case "PB0":
            _ws2812_pb0(val)
        case "PB1":
            _ws2812_pb1(val)
        case "PB2":
            _ws2812_pb2(val)
        case "PB3":
            _ws2812_pb3(val)
        case "PB4":
            _ws2812_pb4(val)
        case "PB5":
            _ws2812_pb5(val)
        case "PD2":
            _ws2812_pd2(val)
        case "PD3":
            _ws2812_pd3(val)
        case "PD4":
            _ws2812_pd4(val)
        case "PD5":
            _ws2812_pd5(val)
        case "PD6":
            _ws2812_pd6(val)
        case "PD7":
            _ws2812_pd7(val)
        case _:
            raise CompileError(_REFUSAL)


def _ws2812_pb0(val: uint8):
    """Eight bits of `val` onto PB0, most significant first."""
    b: uint8 = val
    i: uint8 = 8
    while i > 0:
        PORTB[0] = 1
        if b >= 128:
            # A one: hold high 12 cycles, 750 ns.
            asm("RJMP .+0")
            asm("RJMP .+0")
            asm("RJMP .+0")
            asm("RJMP .+0")
            PORTB[0] = 0
        else:
            # A zero: drop at 7 cycles, 437 ns, then pad the low out to 14, 875 ns.
            asm("NOP")
            asm("NOP")
            PORTB[0] = 0
            asm("NOP")
            asm("NOP")
            asm("NOP")
        b = b << 1
        i = i - 1


def _ws2812_pb1(val: uint8):
    """Eight bits of `val` onto PB1, most significant first."""
    b: uint8 = val
    i: uint8 = 8
    while i > 0:
        PORTB[1] = 1
        if b >= 128:
            # A one: hold high 12 cycles, 750 ns.
            asm("RJMP .+0")
            asm("RJMP .+0")
            asm("RJMP .+0")
            asm("RJMP .+0")
            PORTB[1] = 0
        else:
            # A zero: drop at 7 cycles, 437 ns, then pad the low out to 14, 875 ns.
            asm("NOP")
            asm("NOP")
            PORTB[1] = 0
            asm("NOP")
            asm("NOP")
            asm("NOP")
        b = b << 1
        i = i - 1


def _ws2812_pb2(val: uint8):
    """Eight bits of `val` onto PB2, most significant first."""
    b: uint8 = val
    i: uint8 = 8
    while i > 0:
        PORTB[2] = 1
        if b >= 128:
            # A one: hold high 12 cycles, 750 ns.
            asm("RJMP .+0")
            asm("RJMP .+0")
            asm("RJMP .+0")
            asm("RJMP .+0")
            PORTB[2] = 0
        else:
            # A zero: drop at 7 cycles, 437 ns, then pad the low out to 14, 875 ns.
            asm("NOP")
            asm("NOP")
            PORTB[2] = 0
            asm("NOP")
            asm("NOP")
            asm("NOP")
        b = b << 1
        i = i - 1


def _ws2812_pb3(val: uint8):
    """Eight bits of `val` onto PB3, most significant first."""
    b: uint8 = val
    i: uint8 = 8
    while i > 0:
        PORTB[3] = 1
        if b >= 128:
            # A one: hold high 12 cycles, 750 ns.
            asm("RJMP .+0")
            asm("RJMP .+0")
            asm("RJMP .+0")
            asm("RJMP .+0")
            PORTB[3] = 0
        else:
            # A zero: drop at 7 cycles, 437 ns, then pad the low out to 14, 875 ns.
            asm("NOP")
            asm("NOP")
            PORTB[3] = 0
            asm("NOP")
            asm("NOP")
            asm("NOP")
        b = b << 1
        i = i - 1


def _ws2812_pb4(val: uint8):
    """Eight bits of `val` onto PB4, most significant first."""
    b: uint8 = val
    i: uint8 = 8
    while i > 0:
        PORTB[4] = 1
        if b >= 128:
            # A one: hold high 12 cycles, 750 ns.
            asm("RJMP .+0")
            asm("RJMP .+0")
            asm("RJMP .+0")
            asm("RJMP .+0")
            PORTB[4] = 0
        else:
            # A zero: drop at 7 cycles, 437 ns, then pad the low out to 14, 875 ns.
            asm("NOP")
            asm("NOP")
            PORTB[4] = 0
            asm("NOP")
            asm("NOP")
            asm("NOP")
        b = b << 1
        i = i - 1


def _ws2812_pb5(val: uint8):
    """Eight bits of `val` onto PB5, most significant first."""
    b: uint8 = val
    i: uint8 = 8
    while i > 0:
        PORTB[5] = 1
        if b >= 128:
            # A one: hold high 12 cycles, 750 ns.
            asm("RJMP .+0")
            asm("RJMP .+0")
            asm("RJMP .+0")
            asm("RJMP .+0")
            PORTB[5] = 0
        else:
            # A zero: drop at 7 cycles, 437 ns, then pad the low out to 14, 875 ns.
            asm("NOP")
            asm("NOP")
            PORTB[5] = 0
            asm("NOP")
            asm("NOP")
            asm("NOP")
        b = b << 1
        i = i - 1


def _ws2812_pd2(val: uint8):
    """Eight bits of `val` onto PD2, most significant first."""
    b: uint8 = val
    i: uint8 = 8
    while i > 0:
        PORTD[2] = 1
        if b >= 128:
            # A one: hold high 12 cycles, 750 ns.
            asm("RJMP .+0")
            asm("RJMP .+0")
            asm("RJMP .+0")
            asm("RJMP .+0")
            PORTD[2] = 0
        else:
            # A zero: drop at 7 cycles, 437 ns, then pad the low out to 14, 875 ns.
            asm("NOP")
            asm("NOP")
            PORTD[2] = 0
            asm("NOP")
            asm("NOP")
            asm("NOP")
        b = b << 1
        i = i - 1


def _ws2812_pd3(val: uint8):
    """Eight bits of `val` onto PD3, most significant first."""
    b: uint8 = val
    i: uint8 = 8
    while i > 0:
        PORTD[3] = 1
        if b >= 128:
            # A one: hold high 12 cycles, 750 ns.
            asm("RJMP .+0")
            asm("RJMP .+0")
            asm("RJMP .+0")
            asm("RJMP .+0")
            PORTD[3] = 0
        else:
            # A zero: drop at 7 cycles, 437 ns, then pad the low out to 14, 875 ns.
            asm("NOP")
            asm("NOP")
            PORTD[3] = 0
            asm("NOP")
            asm("NOP")
            asm("NOP")
        b = b << 1
        i = i - 1


def _ws2812_pd4(val: uint8):
    """Eight bits of `val` onto PD4, most significant first."""
    b: uint8 = val
    i: uint8 = 8
    while i > 0:
        PORTD[4] = 1
        if b >= 128:
            # A one: hold high 12 cycles, 750 ns.
            asm("RJMP .+0")
            asm("RJMP .+0")
            asm("RJMP .+0")
            asm("RJMP .+0")
            PORTD[4] = 0
        else:
            # A zero: drop at 7 cycles, 437 ns, then pad the low out to 14, 875 ns.
            asm("NOP")
            asm("NOP")
            PORTD[4] = 0
            asm("NOP")
            asm("NOP")
            asm("NOP")
        b = b << 1
        i = i - 1


def _ws2812_pd5(val: uint8):
    """Eight bits of `val` onto PD5, most significant first."""
    b: uint8 = val
    i: uint8 = 8
    while i > 0:
        PORTD[5] = 1
        if b >= 128:
            # A one: hold high 12 cycles, 750 ns.
            asm("RJMP .+0")
            asm("RJMP .+0")
            asm("RJMP .+0")
            asm("RJMP .+0")
            PORTD[5] = 0
        else:
            # A zero: drop at 7 cycles, 437 ns, then pad the low out to 14, 875 ns.
            asm("NOP")
            asm("NOP")
            PORTD[5] = 0
            asm("NOP")
            asm("NOP")
            asm("NOP")
        b = b << 1
        i = i - 1


def _ws2812_pd6(val: uint8):
    """Eight bits of `val` onto PD6, most significant first."""
    b: uint8 = val
    i: uint8 = 8
    while i > 0:
        PORTD[6] = 1
        if b >= 128:
            # A one: hold high 12 cycles, 750 ns.
            asm("RJMP .+0")
            asm("RJMP .+0")
            asm("RJMP .+0")
            asm("RJMP .+0")
            PORTD[6] = 0
        else:
            # A zero: drop at 7 cycles, 437 ns, then pad the low out to 14, 875 ns.
            asm("NOP")
            asm("NOP")
            PORTD[6] = 0
            asm("NOP")
            asm("NOP")
            asm("NOP")
        b = b << 1
        i = i - 1


def _ws2812_pd7(val: uint8):
    """Eight bits of `val` onto PD7, most significant first."""
    b: uint8 = val
    i: uint8 = 8
    while i > 0:
        PORTD[7] = 1
        if b >= 128:
            # A one: hold high 12 cycles, 750 ns.
            asm("RJMP .+0")
            asm("RJMP .+0")
            asm("RJMP .+0")
            asm("RJMP .+0")
            PORTD[7] = 0
        else:
            # A zero: drop at 7 cycles, 437 ns, then pad the low out to 14, 875 ns.
            asm("NOP")
            asm("NOP")
            PORTD[7] = 0
            asm("NOP")
            asm("NOP")
            asm("NOP")
        b = b << 1
        i = i - 1


@inline
def ws2812_reset(pin: str):
    # Hold the line low past 50 us. The strip takes that as end-of-frame and shows
    # what it was sent.
    match pin:
        case "PB0":
            PORTB[0] = 0
        case "PB1":
            PORTB[1] = 0
        case "PB2":
            PORTB[2] = 0
        case "PB3":
            PORTB[3] = 0
        case "PB4":
            PORTB[4] = 0
        case "PB5":
            PORTB[5] = 0
        case "PD2":
            PORTD[2] = 0
        case "PD3":
            PORTD[3] = 0
        case "PD4":
            PORTD[4] = 0
        case "PD5":
            PORTD[5] = 0
        case "PD6":
            PORTD[6] = 0
        case "PD7":
            PORTD[7] = 0
        case _:
            raise CompileError(_REFUSAL)
    delay_us(55)
