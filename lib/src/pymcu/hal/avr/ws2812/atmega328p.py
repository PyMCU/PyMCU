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
# zero by how long the data line stays high. At 16 MHz a cycle is 62.5 ns and the
# whole budget is twenty of them:
#
#   0 bit   high  6 cycles (375 ns)   low 14 cycles
#   1 bit   high 13 cycles (812 ns)   low  7 cycles
#   period       20 cycles (1.25 us)
#   reset        the line held low for more than 50 us
#
# WS2812B allows 150 ns either way on the high times, which is 2.4 cycles. That is
# the entire margin, and it is why this is written as SBI/CBI with counted NOPs
# rather than as anything a register allocator is free to rearrange.
#
# `_ws2812_b` and `_ws2812_d` are deliberately NOT @inline. They hold the only loops
# in the file, and a label inside an inlined body is emitted once per call site,
# which the assembler rejects as a duplicate. The pin dispatch around them IS
# @inline, so a constant pin folds every non-matching arm away and what survives is
# one port and one bit.
#
# Interrupts have to be off for the duration: one interrupt taken mid-byte stretches
# a high time past its tolerance and the strip latches the wrong colour. That is the
# caller's to own, because the caller is what knows how long the frame is.
# -----------------------------------------------------------------------------
from pymcu.exceptions import CompileError
from pymcu.types import uint8, uint16, inline, ptr, asm
from pymcu.chips.atmega328p import PORTB, PORTD, DDRB, DDRD
from pymcu.time import delay_us


@inline
def ws2812_init(pin: str):
    # Configure the data pin as output and hold low.
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
            raise CompileError("NeoPixel: unsupported data pin -- use PB0-PB5 or PD2-PD7")


@inline
def ws2812_write_byte(pin: str, val: uint8):
    # Dispatch to port-specific implementation by pin name.
    # The compiler folds away all non-matching branches at compile time.
    match pin:
        case "PB0":
            _ws2812_b(0, val)
        case "PB1":
            _ws2812_b(1, val)
        case "PB2":
            _ws2812_b(2, val)
        case "PB3":
            _ws2812_b(3, val)
        case "PB4":
            _ws2812_b(4, val)
        case "PB5":
            _ws2812_b(5, val)
        case "PD2":
            _ws2812_d(2, val)
        case "PD3":
            _ws2812_d(3, val)
        case "PD4":
            _ws2812_d(4, val)
        case "PD5":
            _ws2812_d(5, val)
        case "PD6":
            _ws2812_d(6, val)
        case "PD7":
            _ws2812_d(7, val)
        case _:
            raise CompileError("NeoPixel: unsupported data pin -- use PB0-PB5 or PD2-PD7")


# Non-inline function: sends one byte MSB-first to PORTB at the given bit index.
# Being non-inline means the asm labels inside appear exactly once per function.
# R24=val, R22=bit (0-5 for PB0-PB5 on PORTB IO addr 0x05).
def _ws2812_b(bit: uint8, val: uint8):
    # PORTB IO address = 0x05; SBI 0x05,bit sets the pin.
    # Loop 8 times, MSB first. Each bit period = 20 cycles (1.25 us at 16 MHz).
    # 0-bit: 6 cy HIGH, 14 cy LOW
    # 1-bit: 13 cy HIGH, 7 cy LOW
    #
    # R16 = counter (8), R17 = working byte copy
    # Use SBI/CBI for atomic single-bit writes to PORTB.
    #
    # Inner loop (not labeled -- avoids duplicate label in asm output):
    # We emit the timing via NOP sequences rather than labeled loops
    # to satisfy the constraint that labels in @inline functions must use
    # non-inline sub-helpers. This function IS non-inline so labels are safe.
    i: uint8 = 8
    b: uint8 = val
    while i > 0:
        # Set pin HIGH (2 cycles via SBI)
        match bit:
            case 0:
                PORTB[0] = 1
                if b >= 128:
                    asm("NOP")
                    asm("NOP")
                    asm("NOP")
                    asm("NOP")
                    asm("NOP")
                    asm("NOP")
                else:
                    asm("NOP")
                    asm("NOP")
                    PORTB[0] = 0
                PORTB[0] = 0
            case 1:
                PORTB[1] = 1
                if b >= 128:
                    asm("NOP")
                    asm("NOP")
                    asm("NOP")
                    asm("NOP")
                    asm("NOP")
                    asm("NOP")
                else:
                    asm("NOP")
                    asm("NOP")
                    PORTB[1] = 0
                PORTB[1] = 0
            case 2:
                PORTB[2] = 1
                if b >= 128:
                    asm("NOP")
                    asm("NOP")
                    asm("NOP")
                    asm("NOP")
                    asm("NOP")
                    asm("NOP")
                else:
                    asm("NOP")
                    asm("NOP")
                    PORTB[2] = 0
                PORTB[2] = 0
            case 3:
                PORTB[3] = 1
                if b >= 128:
                    asm("NOP")
                    asm("NOP")
                    asm("NOP")
                    asm("NOP")
                    asm("NOP")
                    asm("NOP")
                else:
                    asm("NOP")
                    asm("NOP")
                    PORTB[3] = 0
                PORTB[3] = 0
            case 4:
                PORTB[4] = 1
                if b >= 128:
                    asm("NOP")
                    asm("NOP")
                    asm("NOP")
                    asm("NOP")
                    asm("NOP")
                    asm("NOP")
                else:
                    asm("NOP")
                    asm("NOP")
                    PORTB[4] = 0
                PORTB[4] = 0
            case 5:
                PORTB[5] = 1
                if b >= 128:
                    asm("NOP")
                    asm("NOP")
                    asm("NOP")
                    asm("NOP")
                    asm("NOP")
                    asm("NOP")
                else:
                    asm("NOP")
                    asm("NOP")
                    PORTB[5] = 0
                PORTB[5] = 0
            case _:
                pass
        b = b << 1
        i = i - 1


# Non-inline: same as _ws2812_b but for PORTD pins.
def _ws2812_d(bit: uint8, val: uint8):
    i: uint8 = 8
    b: uint8 = val
    while i > 0:
        match bit:
            case 2:
                PORTD[2] = 1
                if b >= 128:
                    asm("NOP")
                    asm("NOP")
                    asm("NOP")
                    asm("NOP")
                    asm("NOP")
                    asm("NOP")
                else:
                    asm("NOP")
                    asm("NOP")
                    PORTD[2] = 0
                PORTD[2] = 0
            case 3:
                PORTD[3] = 1
                if b >= 128:
                    asm("NOP")
                    asm("NOP")
                    asm("NOP")
                    asm("NOP")
                    asm("NOP")
                    asm("NOP")
                else:
                    asm("NOP")
                    asm("NOP")
                    PORTD[3] = 0
                PORTD[3] = 0
            case 4:
                PORTD[4] = 1
                if b >= 128:
                    asm("NOP")
                    asm("NOP")
                    asm("NOP")
                    asm("NOP")
                    asm("NOP")
                    asm("NOP")
                else:
                    asm("NOP")
                    asm("NOP")
                    PORTD[4] = 0
                PORTD[4] = 0
            case 5:
                PORTD[5] = 1
                if b >= 128:
                    asm("NOP")
                    asm("NOP")
                    asm("NOP")
                    asm("NOP")
                    asm("NOP")
                    asm("NOP")
                else:
                    asm("NOP")
                    asm("NOP")
                    PORTD[5] = 0
                PORTD[5] = 0
            case 6:
                PORTD[6] = 1
                if b >= 128:
                    asm("NOP")
                    asm("NOP")
                    asm("NOP")
                    asm("NOP")
                    asm("NOP")
                    asm("NOP")
                else:
                    asm("NOP")
                    asm("NOP")
                    PORTD[6] = 0
                PORTD[6] = 0
            case 7:
                PORTD[7] = 1
                if b >= 128:
                    asm("NOP")
                    asm("NOP")
                    asm("NOP")
                    asm("NOP")
                    asm("NOP")
                    asm("NOP")
                else:
                    asm("NOP")
                    asm("NOP")
                    PORTD[7] = 0
                PORTD[7] = 0
            case _:
                pass
        b = b << 1
        i = i - 1


@inline
def ws2812_reset(pin: str):
    # Hold data line LOW for >50 us (reset pulse).
    # Pin is already configured as output.
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
            raise CompileError("NeoPixel: unsupported data pin -- use PB0-PB5 or PD2-PD7")
    delay_us(55)
