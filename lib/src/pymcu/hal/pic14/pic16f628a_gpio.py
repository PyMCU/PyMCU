# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
from pymcu.chips.pic16f628a import PORTA, PORTB, TRISA, TRISB, CMCON, OPTION_REG
from pymcu.types import uint8, inline
from pymcu.exceptions import CompileError


@inline
def gpio_init():
    """Turn the comparators off so RA0-RA3 are digital.

    They come up analog out of reset; without this a digital read of those pins
    returns 0 for ever, the same trap ANSELx is on the PIC18 parts.
    """
    CMCON.value = 0x07


@inline
def pin_set_mode(name: str, mode: uint8):
    CMCON.value = 0x07
    if name == "RA0":
        TRISA[0] = mode
    elif name == "RA1":
        TRISA[1] = mode
    elif name == "RA2":
        TRISA[2] = mode
    elif name == "RA3":
        TRISA[3] = mode
    elif name == "RA4":
        TRISA[4] = mode
    elif name == "RA5":
        if mode == 0:
            raise CompileError("RA5 on the PIC16F628A is input-only (MCLR/VPP); it cannot be driven")
        TRISA[5] = 1
    elif name == "RA6":
        TRISA[6] = mode
    elif name == "RA7":
        TRISA[7] = mode
    elif name == "RB0":
        TRISB[0] = mode
    elif name == "RB1":
        TRISB[1] = mode
    elif name == "RB2":
        TRISB[2] = mode
    elif name == "RB3":
        TRISB[3] = mode
    elif name == "RB4":
        TRISB[4] = mode
    elif name == "RB5":
        TRISB[5] = mode
    elif name == "RB6":
        TRISB[6] = mode
    elif name == "RB7":
        TRISB[7] = mode
    else:
        raise CompileError("Unknown pin for PIC16F628A")


@inline
def pin_high(name: str):
    if name == "RA0":
        PORTA[0] = 1
    elif name == "RA1":
        PORTA[1] = 1
    elif name == "RA2":
        PORTA[2] = 1
    elif name == "RA3":
        PORTA[3] = 1
    elif name == "RA4":
        raise CompileError("RA4 on the PIC16F628A is open-drain: it can pull low but not drive high; add a pull-up and use pin_low()/pin_set_mode(IN)")
    elif name == "RA6":
        PORTA[6] = 1
    elif name == "RA7":
        PORTA[7] = 1
    elif name == "RB0":
        PORTB[0] = 1
    elif name == "RB1":
        PORTB[1] = 1
    elif name == "RB2":
        PORTB[2] = 1
    elif name == "RB3":
        PORTB[3] = 1
    elif name == "RB4":
        PORTB[4] = 1
    elif name == "RB5":
        PORTB[5] = 1
    elif name == "RB6":
        PORTB[6] = 1
    elif name == "RB7":
        PORTB[7] = 1
    else:
        raise CompileError("Pin cannot be driven high on the PIC16F628A")


@inline
def pin_low(name: str):
    if name == "RA0":
        PORTA[0] = 0
    elif name == "RA1":
        PORTA[1] = 0
    elif name == "RA2":
        PORTA[2] = 0
    elif name == "RA3":
        PORTA[3] = 0
    elif name == "RA4":
        PORTA[4] = 0
    elif name == "RA6":
        PORTA[6] = 0
    elif name == "RA7":
        PORTA[7] = 0
    elif name == "RB0":
        PORTB[0] = 0
    elif name == "RB1":
        PORTB[1] = 0
    elif name == "RB2":
        PORTB[2] = 0
    elif name == "RB3":
        PORTB[3] = 0
    elif name == "RB4":
        PORTB[4] = 0
    elif name == "RB5":
        PORTB[5] = 0
    elif name == "RB6":
        PORTB[6] = 0
    elif name == "RB7":
        PORTB[7] = 0
    else:
        raise CompileError("Pin cannot be driven low on the PIC16F628A")


@inline
def pin_write(name: str, val: uint8):
    if val == 1:
        pin_high(name)
    elif val == 0:
        pin_low(name)


@inline
def pin_toggle(name: str):
    if pin_read(name) == 1:
        pin_low(name)
    else:
        pin_high(name)


@inline
def pin_read(name: str) -> uint8:
    if name == "RA0":
        return PORTA[0]
    elif name == "RA1":
        return PORTA[1]
    elif name == "RA2":
        return PORTA[2]
    elif name == "RA3":
        return PORTA[3]
    elif name == "RA4":
        return PORTA[4]
    elif name == "RA5":
        return PORTA[5]
    elif name == "RA6":
        return PORTA[6]
    elif name == "RA7":
        return PORTA[7]
    elif name == "RB0":
        return PORTB[0]
    elif name == "RB1":
        return PORTB[1]
    elif name == "RB2":
        return PORTB[2]
    elif name == "RB3":
        return PORTB[3]
    elif name == "RB4":
        return PORTB[4]
    elif name == "RB5":
        return PORTB[5]
    elif name == "RB6":
        return PORTB[6]
    elif name == "RB7":
        return PORTB[7]
    else:
        raise CompileError("Unknown pin for PIC16F628A")


@inline
def pin_pull_up(name: str):
    """PORTB weak pull-ups are all-or-nothing here: OPTION_REG<7> gates the
    whole port, and a pin only gets one while it is an input."""
    if name == "RB0":
        OPTION_REG[7] = 0
    elif name == "RB1":
        OPTION_REG[7] = 0
    elif name == "RB2":
        OPTION_REG[7] = 0
    elif name == "RB3":
        OPTION_REG[7] = 0
    elif name == "RB4":
        OPTION_REG[7] = 0
    elif name == "RB5":
        OPTION_REG[7] = 0
    elif name == "RB6":
        OPTION_REG[7] = 0
    elif name == "RB7":
        OPTION_REG[7] = 0
    else:
        raise CompileError("Weak pull-ups on the PIC16F628A exist on PORTB only")


@inline
def pin_pull_off(name: str):
    if name == "RB0":
        OPTION_REG[7] = 1
    elif name == "RB1":
        OPTION_REG[7] = 1
    elif name == "RB2":
        OPTION_REG[7] = 1
    elif name == "RB3":
        OPTION_REG[7] = 1
    elif name == "RB4":
        OPTION_REG[7] = 1
    elif name == "RB5":
        OPTION_REG[7] = 1
    elif name == "RB6":
        OPTION_REG[7] = 1
    elif name == "RB7":
        OPTION_REG[7] = 1
    else:
        raise CompileError("Weak pull-ups on the PIC16F628A exist on PORTB only")
