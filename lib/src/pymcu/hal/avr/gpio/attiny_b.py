# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
#
# ATtiny GPIO HAL -- single-port chips (ATtiny85/45/25/13/13a)
#
# All GPIO is on PORTB only.  PB5 is the RESET pin; treat with care.
#
# DATA addresses (I/O address + 0x20):
#   PINB  = 0x36  (I/O 0x16)
#   DDRB  = 0x37  (I/O 0x17)
#   PORTB = 0x38  (I/O 0x18)
# -----------------------------------------------------------------------------

from pymcu.chips.attiny85 import DDRB, PORTB, PINB
from pymcu.types import uint8, uint16, inline, ptr, const
from pymcu.exceptions import CompileError

@inline
def select_port(name: const) -> ptr[uint8]:
    match name:
        case 'PB0' | 'PB1' | 'PB2' | 'PB3' | 'PB4' | 'PB5':
            return PORTB
        case _:
            raise NotImplementedError('Unsupported Pin')

@inline
def select_ddr(name: const) -> ptr[uint8]:
    match name:
        case 'PB0' | 'PB1' | 'PB2' | 'PB3' | 'PB4' | 'PB5':
            return DDRB
        case _:
            raise NotImplementedError('Unsupported Pin')

@inline
def select_pin(name: const) -> ptr[uint8]:
    match name:
        case 'PB0' | 'PB1' | 'PB2' | 'PB3' | 'PB4' | 'PB5':
            return PINB
        case _:
            raise NotImplementedError('Unsupported Pin')

@inline
def select_bit(name: const) -> uint8:
    match name:
        case 'PB0':
            return 0
        case 'PB1':
            return 1
        case 'PB2':
            return 2
        case 'PB3':
            return 3
        case 'PB4':
            return 4
        case 'PB5':
            return 5
        case _:
            raise NotImplementedError('Unsupported Pin')


# No board numbering: these are bare chips with no silkscreen to number. Refusing
# here is what stops `Pin(13)` from resolving to PB5, which on this part is RESET
# and needs the RSTDISBL fuse -- after which the chip can no longer be programmed
# over ISP. That has to be something you ask for by name, not something you get
# by pasting an Arduino blink.
@inline
def board_pin_name(n: const[uint8]) -> str:
    raise CompileError(
        "board pin numbers are not supported on this ATtiny: it is a bare chip "
        "with no Arduino numbering. Give a PORT NAME instead (PB0-PB5). Note "
        "that PB5 is the RESET pin and needs the RSTDISBL fuse to drive an LED.")
