# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
#
# ATtiny GPIO HAL -- ATtiny2313/4313
#
# GPIO is on PORTD (PD0-PD6) and PORTB (PB0-PB7).
#
# DATA addresses (I/O address + 0x20):
#   PIND  = 0x30  (I/O 0x10)
#   DDRD  = 0x31  (I/O 0x11)
#   PORTD = 0x32  (I/O 0x12)
#   PINB  = 0x36  (I/O 0x16)
#   DDRB  = 0x37  (I/O 0x17)
#   PORTB = 0x38  (I/O 0x18)
# -----------------------------------------------------------------------------

from pymcu.chips.attiny2313 import DDRD, DDRB, PORTD, PORTB, PIND, PINB
from pymcu.types import uint8, uint16, inline, ptr, const
from pymcu.exceptions import CompileError

@inline
def select_port(name: const) -> ptr[uint8]:
    match name:
        case 'PD0' | 'PD1' | 'PD2' | 'PD3' | 'PD4' | 'PD5' | 'PD6':
            return PORTD
        case 'PB0' | 'PB1' | 'PB2' | 'PB3' | 'PB4' | 'PB5' | 'PB6' | 'PB7':
            return PORTB
        case _:
            raise NotImplementedError('Unsupported Pin')

@inline
def select_ddr(name: const) -> ptr[uint8]:
    match name:
        case 'PD0' | 'PD1' | 'PD2' | 'PD3' | 'PD4' | 'PD5' | 'PD6':
            return DDRD
        case 'PB0' | 'PB1' | 'PB2' | 'PB3' | 'PB4' | 'PB5' | 'PB6' | 'PB7':
            return DDRB
        case _:
            raise NotImplementedError('Unsupported Pin')

@inline
def select_pin(name: const) -> ptr[uint8]:
    match name:
        case 'PD0' | 'PD1' | 'PD2' | 'PD3' | 'PD4' | 'PD5' | 'PD6':
            return PIND
        case 'PB0' | 'PB1' | 'PB2' | 'PB3' | 'PB4' | 'PB5' | 'PB6' | 'PB7':
            return PINB
        case _:
            raise NotImplementedError('Unsupported Pin')

@inline
def select_bit(name: const) -> uint8:
    match name:
        case 'PD0' | 'PB0':
            return 0
        case 'PD1' | 'PB1':
            return 1
        case 'PD2' | 'PB2':
            return 2
        case 'PD3' | 'PB3':
            return 3
        case 'PD4' | 'PB4':
            return 4
        case 'PD5' | 'PB5':
            return 5
        case 'PD6' | 'PB6':
            return 6
        case 'PB7':
            return 7
        case _:
            raise NotImplementedError('Unsupported Pin')


# No board numbering: bare chip, no silkscreen to number. See attiny_b.py.
@inline
def board_pin_name(n: const[uint8]) -> str:
    raise CompileError(
        "board pin numbers are not supported on this ATtiny: it is a bare chip "
        "with no Arduino numbering. Give a PORT NAME instead (PB0-PB7, PD0-PD6).")
