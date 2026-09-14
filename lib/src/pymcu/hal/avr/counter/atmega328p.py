# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
#
# Counting edges on a pin -- ATmega48/88/168/328 family
#
# A pin interrupt and a 32-bit counter. That is the whole mechanism, and it is the right
# one here: the part's hardware counters are the timers, and every one of them is already
# spoken for -- Timer0 is the millisecond time base, Timer1 times pulses and drives the
# servo channels, Timer2 carries the infrared carrier. A timer's external clock input is
# also T0 (PD4) or T1 (PD5) and nothing else, while an interrupt counts on any of 23 pins.
#
# What it costs is the interrupt: about 30 cycles an edge, so above roughly 100 kHz the
# part spends all its time counting. A flow meter or a tachometer is orders of magnitude
# below that.
#
# WHICH EDGES. INT0 (PD2) and INT1 (PD3) select rising, falling or both in hardware. Every
# other pin has only a pin-change interrupt, which fires on both and cannot tell them apart
# without reading the pin back, so asking one of those for a single edge is refused rather
# than counted twice.
# -----------------------------------------------------------------------------
from pymcu.chips.atmega328p import SREG
from pymcu.exceptions import CompileError
from pymcu.types import uint8, uint16, uint32, inline, const, compile_isr
from pymcu.hal.avr.gpio.atmega328p import pin_irq_enable

_count: uint32 = 0


# NOT @inline: the vector jumps here, so it needs an address.
def counter_isr():
    global _count
    _count = _count + 1


# The trigger code pin_irq_enable takes, for an edge this pin can actually distinguish.
# 0 = both edges, 1 = rising, 2 = falling, which is countio's numbering and not the GPIO
# HAL's; the translation is here so that nothing above has to know either.
@inline
def counter_trigger(pin: const, edge: const[uint8]) -> uint8:
    if edge > 2:
        raise CompileError(
            "an edge is 0 (both), 1 (rising) or 2 (falling). Pass one of those.")
    match pin:
        case 'PD2' | 2 | 'PD3' | 3:
            if edge == 1:
                return 2      # the GPIO HAL's rising
            if edge == 2:
                return 1      # the GPIO HAL's falling
            return 3          # any edge
        case _:
            if edge != 0:
                raise CompileError(
                    "this pin can only count BOTH edges. Rising and falling are told apart "
                    "by INT0 and INT1, which are PD2 and PD3 (D2 and D3 on an Arduino "
                    "board); every other pin has a pin-change interrupt that fires on both "
                    "and cannot say which. Move the signal to D2 or D3, or count both edges "
                    "and halve the number.")
            return 3


@inline
def counter_attach(pin: const, edge: const[uint8]):
    pin_irq_enable(pin, counter_trigger(pin, edge))
    # The vector is written out rather than taken from a helper: compile_isr needs a
    # compile-time constant and an @inline function's return value is not accepted as one
    # (PyMCU#321).
    match pin:
        case 'PD2' | 2:
            compile_isr(counter_isr, 0x0002)
        case 'PD3' | 3:
            compile_isr(counter_isr, 0x0004)
        case 'PB0' | 'PB1' | 'PB2' | 'PB3' | 'PB4' | 'PB5' | 8 | 9 | 10 | 11 | 12 | 13:
            compile_isr(counter_isr, 0x0006)
        case 'PC0' | 'PC1' | 'PC2' | 'PC3' | 'PC4' | 'PC5' | 14 | 15 | 16 | 17 | 18 | 19:
            compile_isr(counter_isr, 0x0008)
        case 'PD0' | 'PD1' | 'PD4' | 'PD5' | 'PD6' | 'PD7' | 0 | 1 | 4 | 5 | 6 | 7:
            compile_isr(counter_isr, 0x000A)


# Read the four bytes with the interrupt held off, so an edge cannot land between two of
# them and hand back a number that was never the count.
def counter_read() -> uint32:
    global _count
    SREG[7] = 0
    v: uint32 = _count
    SREG[7] = 1
    return v


def counter_reset():
    global _count
    SREG[7] = 0
    _count = 0
    SREG[7] = 1
