# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
#
# Quadrature decoding -- ATmega48/88/168/328 family
#
# Two lines and an interrupt on each. The state is (A << 1) | B and it walks 00, 01, 11, 10
# one way and 00, 10, 11, 01 the other, so for any single-step move the OLD state's high bit
# exclusive-ored with the NEW state's low bit is 1 going forward and 0 going back. Nothing
# changing, and both lines changing at once, are not a direction: the second is a bounce or
# an edge that arrived while the handler was busy, and counting it would be inventing a
# direction the knob never took.
#
# It was a sixteen-entry lookup table, read from inside the handler. Two things were wrong
# with that and the arithmetic settles both: a table is RAM the decode does not need, and
# the search for why the position never moved was much harder with one in the way.
#
# BOTH LINES ON ONE PORT. Each handler below reads its OWN port register, because a register
# address has to be known when the firmware is built: a handler is a top-level function and
# cannot be specialised for the pins a program picked. What varies per program is the two bit
# MASKS, which are plain bytes and can be globals. A pair on two ports is refused.
#
# ONE HANDLER PER VECTOR, NOT PER PORT. PD2 and PD3 are INT0 and INT1 and have vectors of
# their own, and they are the pair every encoder guide wires a knob to. Registering one
# function at two vectors keeps only the last of them and leaves the first pointing at the
# bad-interrupt handler (PyMCU#325), so each vector gets its own entry point and they all
# call one shared step.
#
# WHY THE POSITION IS WRITTEN BY A PLAIN FUNCTION AND NOT BY THE HANDLER ITSELF. It is, now.
# The first version of this file decoded correctly and still read zero for ever, because a
# global shared between a handler and the main program was allocated in the callee-saved
# pool R2-R15 and every handler epilogue restored it, undoing the write it had just made
# (PyMCU#328, fixed in the AVR backend). Nothing in this file works around it.
#
# The cost is the interrupt, about 40 cycles an edge. A hand-turned knob makes a few hundred
# edges a second; an encoder fast enough to matter would swamp the part.
# -----------------------------------------------------------------------------
from pymcu.chips.atmega328p import PINB, PINC, PIND, SREG
from pymcu.exceptions import CompileError
from pymcu.types import uint8, int32, inline, const, compile_isr
from pymcu.hal.avr.gpio.atmega328p import pin_irq_enable

_state:  uint8 = 0
_pos:    int32 = 0
_mask_a: uint8 = 0
_mask_b: uint8 = 0


# The decoding itself, once. NOT @inline: the five entry points below all call it, and five
# copies would be five copies in flash.
def encoder_step(v: uint8):
    global _state, _pos, _mask_a, _mask_b
    now: uint8 = 0
    if v & _mask_a:
        now = 2
    if v & _mask_b:
        now = now + 1
    moved: uint8 = _state ^ now
    if moved != 0 and moved != 3:
        if ((_state >> 1) ^ now) & 1:
            _pos = _pos + 1
        else:
            _pos = _pos - 1
    _state = now


# The state the two lines are showing right now, without counting it as a move. An encoder
# idles with both lines released, which with the pull-ups on reads 11 and not 00; starting
# the decoder at 00 made the very first edge look like a step that never happened.
@inline
def encoder_prime(v: uint8):
    global _state, _mask_a, _mask_b
    s: uint8 = 0
    if v & _mask_a:
        s = 2
    if v & _mask_b:
        s = s + 1
    _state = s


# One entry point per vector. NOT @inline: a vector jumps here, so each needs an address.
def encoder_isr_int0():
    encoder_step(PIND.value)


def encoder_isr_int1():
    encoder_step(PIND.value)


def encoder_isr_b():
    encoder_step(PINB.value)


def encoder_isr_c():
    encoder_step(PINC.value)


def encoder_isr_d():
    encoder_step(PIND.value)


# The bit the pin occupies in its port register, as the mask the handler tests.
@inline
def encoder_mask(pin: const) -> uint8:
    match pin:
        case 'PB0' | 'PC0' | 'PD0' | 0 | 8 | 14:
            return 0x01
        case 'PB1' | 'PC1' | 'PD1' | 1 | 9 | 15:
            return 0x02
        case 'PB2' | 'PC2' | 'PD2' | 2 | 10 | 16:
            return 0x04
        case 'PB3' | 'PC3' | 'PD3' | 3 | 11 | 17:
            return 0x08
        case 'PB4' | 'PC4' | 'PD4' | 4 | 12 | 18:
            return 0x10
        case 'PB5' | 'PC5' | 'PD5' | 5 | 13 | 19:
            return 0x20
        case 'PD6' | 6:
            return 0x40
        case 'PD7' | 7:
            return 0x80
        case _:
            raise CompileError(
                "this pin is not one this chip can interrupt on. Name it as 'PB0' to "
                "'PB5', 'PC0' to 'PC5', 'PD0' to 'PD7', or as the Arduino board number 0 "
                "to 19.")


# The two refusals below are nested matches on the two names, not a comparison of a port
# number worked out for each. The comparison is decided when the firmware is built, but
# the compiler does not see through an @inline call in the condition of an `if`: it reports
# the guard as unverifiable and leaves the message in the binary as runtime code. A match
# on a const parameter folds away entirely, so these arms cost nothing.
@inline
def encoder_check_same_port(pin_a: const, pin_b: const):
    match pin_a:
        case 'PB0' | 'PB1' | 'PB2' | 'PB3' | 'PB4' | 'PB5' | 8 | 9 | 10 | 11 | 12 | 13:
            match pin_b:
                case 'PB0' | 'PB1' | 'PB2' | 'PB3' | 'PB4' | 'PB5' | 8 | 9 | 10 | 11 | 12 | 13:
                    pass
                case _:
                    raise CompileError(
                        "an encoder's two lines have to be on the same port. The handler that "
                        "decodes them reads one port register, and a register address has to be "
                        "known when the firmware is built, so it cannot read a second port chosen "
                        "by the program. On an Arduino Uno that means both lines among D0 to D7, "
                        "or both among D8 to D13, or both among A0 to A5. Move one of the two.")
        case 'PC0' | 'PC1' | 'PC2' | 'PC3' | 'PC4' | 'PC5' | 14 | 15 | 16 | 17 | 18 | 19:
            match pin_b:
                case 'PC0' | 'PC1' | 'PC2' | 'PC3' | 'PC4' | 'PC5' | 14 | 15 | 16 | 17 | 18 | 19:
                    pass
                case _:
                    raise CompileError(
                        "an encoder's two lines have to be on the same port. The handler that "
                        "decodes them reads one port register, and a register address has to be "
                        "known when the firmware is built, so it cannot read a second port chosen "
                        "by the program. On an Arduino Uno that means both lines among D0 to D7, "
                        "or both among D8 to D13, or both among A0 to A5. Move one of the two.")
        case 'PD0' | 'PD1' | 'PD2' | 'PD3' | 'PD4' | 'PD5' | 'PD6' | 'PD7' | 0 | 1 | 2 | 3 | 4 | 5 | 6 | 7:
            match pin_b:
                case 'PD0' | 'PD1' | 'PD2' | 'PD3' | 'PD4' | 'PD5' | 'PD6' | 'PD7' | 0 | 1 | 2 | 3 | 4 | 5 | 6 | 7:
                    pass
                case _:
                    raise CompileError(
                        "an encoder's two lines have to be on the same port. The handler that "
                        "decodes them reads one port register, and a register address has to be "
                        "known when the firmware is built, so it cannot read a second port chosen "
                        "by the program. On an Arduino Uno that means both lines among D0 to D7, "
                        "or both among D8 to D13, or both among A0 to A5. Move one of the two.")
        case _:
            raise CompileError(
                "this pin is not one this chip can interrupt on. Name it as 'PB0' to "
                "'PB5', 'PC0' to 'PC5', 'PD0' to 'PD7', or as the Arduino board number 0 "
                "to 19.")


@inline
def encoder_check_two_pins(pin_a: const, pin_b: const):
    # Reached with both lines already known to be on one port, so the same bit is the
    # same pin.
    match pin_a:
        case 'PB0' | 'PC0' | 'PD0' | 0 | 8 | 14:
            match pin_b:
                case 'PB0' | 'PC0' | 'PD0' | 0 | 8 | 14:
                    raise CompileError(
                        "an encoder's two lines have to be two different pins. Both names given "
                        "are the same pin, so there is only one line and nothing to tell a "
                        "direction from.")
                case _:
                    pass
        case 'PB1' | 'PC1' | 'PD1' | 1 | 9 | 15:
            match pin_b:
                case 'PB1' | 'PC1' | 'PD1' | 1 | 9 | 15:
                    raise CompileError(
                        "an encoder's two lines have to be two different pins. Both names given "
                        "are the same pin, so there is only one line and nothing to tell a "
                        "direction from.")
                case _:
                    pass
        case 'PB2' | 'PC2' | 'PD2' | 2 | 10 | 16:
            match pin_b:
                case 'PB2' | 'PC2' | 'PD2' | 2 | 10 | 16:
                    raise CompileError(
                        "an encoder's two lines have to be two different pins. Both names given "
                        "are the same pin, so there is only one line and nothing to tell a "
                        "direction from.")
                case _:
                    pass
        case 'PB3' | 'PC3' | 'PD3' | 3 | 11 | 17:
            match pin_b:
                case 'PB3' | 'PC3' | 'PD3' | 3 | 11 | 17:
                    raise CompileError(
                        "an encoder's two lines have to be two different pins. Both names given "
                        "are the same pin, so there is only one line and nothing to tell a "
                        "direction from.")
                case _:
                    pass
        case 'PB4' | 'PC4' | 'PD4' | 4 | 12 | 18:
            match pin_b:
                case 'PB4' | 'PC4' | 'PD4' | 4 | 12 | 18:
                    raise CompileError(
                        "an encoder's two lines have to be two different pins. Both names given "
                        "are the same pin, so there is only one line and nothing to tell a "
                        "direction from.")
                case _:
                    pass
        case 'PB5' | 'PC5' | 'PD5' | 5 | 13 | 19:
            match pin_b:
                case 'PB5' | 'PC5' | 'PD5' | 5 | 13 | 19:
                    raise CompileError(
                        "an encoder's two lines have to be two different pins. Both names given "
                        "are the same pin, so there is only one line and nothing to tell a "
                        "direction from.")
                case _:
                    pass
        case 'PD6' | 6:
            match pin_b:
                case 'PD6' | 6:
                    raise CompileError(
                        "an encoder's two lines have to be two different pins. Both names given "
                        "are the same pin, so there is only one line and nothing to tell a "
                        "direction from.")
                case _:
                    pass
        case 'PD7' | 7:
            match pin_b:
                case 'PD7' | 7:
                    raise CompileError(
                        "an encoder's two lines have to be two different pins. Both names given "
                        "are the same pin, so there is only one line and nothing to tell a "
                        "direction from.")
                case _:
                    pass
        case _:
            pass


@inline
def encoder_attach(pin_a: const, pin_b: const):
    encoder_check_same_port(pin_a, pin_b)
    encoder_check_two_pins(pin_a, pin_b)
    global _mask_a, _mask_b, _state, _pos
    _mask_a = encoder_mask(pin_a)
    _mask_b = encoder_mask(pin_b)
    _pos = 0
    match pin_a:
        case 'PB0' | 'PB1' | 'PB2' | 'PB3' | 'PB4' | 'PB5' | 8 | 9 | 10 | 11 | 12 | 13:
            encoder_prime(PINB.value)
        case 'PC0' | 'PC1' | 'PC2' | 'PC3' | 'PC4' | 'PC5' | 14 | 15 | 16 | 17 | 18 | 19:
            encoder_prime(PINC.value)
        case _:
            encoder_prime(PIND.value)
    pin_irq_enable(pin_a, 3)   # 3 = any edge, which is what a quadrature line gives
    pin_irq_enable(pin_b, 3)
    # The vectors are written out rather than returned by a helper: compile_isr needs a
    # compile-time constant and an @inline function's return value is not accepted as one
    # (PyMCU#321). PD2 and PD3 have a vector each; every other pin shares its port's, and
    # registering the same handler twice at one vector is the same registration, so the
    # pair on a pin-change port names its handler once here and once below with no harm.
    match pin_a:
        case 'PD2' | 2:
            compile_isr(encoder_isr_int0, 0x0002)
        case 'PD3' | 3:
            compile_isr(encoder_isr_int1, 0x0004)
        case 'PB0' | 'PB1' | 'PB2' | 'PB3' | 'PB4' | 'PB5' | 8 | 9 | 10 | 11 | 12 | 13:
            compile_isr(encoder_isr_b, 0x0006)
        case 'PC0' | 'PC1' | 'PC2' | 'PC3' | 'PC4' | 'PC5' | 14 | 15 | 16 | 17 | 18 | 19:
            compile_isr(encoder_isr_c, 0x0008)
        case _:
            compile_isr(encoder_isr_d, 0x000A)
    match pin_b:
        case 'PD2' | 2:
            compile_isr(encoder_isr_int0, 0x0002)
        case 'PD3' | 3:
            compile_isr(encoder_isr_int1, 0x0004)
        case 'PB0' | 'PB1' | 'PB2' | 'PB3' | 'PB4' | 'PB5' | 8 | 9 | 10 | 11 | 12 | 13:
            compile_isr(encoder_isr_b, 0x0006)
        case 'PC0' | 'PC1' | 'PC2' | 'PC3' | 'PC4' | 'PC5' | 14 | 15 | 16 | 17 | 18 | 19:
            compile_isr(encoder_isr_c, 0x0008)
        case _:
            compile_isr(encoder_isr_d, 0x000A)


# Read the four bytes with the interrupt held off, so an edge cannot land between two of
# them and hand back a position the knob was never at.
def encoder_read() -> int32:
    global _pos
    SREG[7] = 0
    v: int32 = _pos
    SREG[7] = 1
    return v


def encoder_write(value: int32):
    global _pos
    SREG[7] = 0
    _pos = value
    SREG[7] = 1
