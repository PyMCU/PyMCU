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

@inline
def select_port(name: str) -> ptr[uint8]:
    match name:
        case 'PB0' | 'PB1' | 'PB2' | 'PB3' | 'PB4' | 'PB5':
            return PORTB
        case _:
            raise NotImplementedError('Unsupported Pin')

@inline
def select_ddr(name: str) -> ptr[uint8]:
    match name:
        case 'PB0' | 'PB1' | 'PB2' | 'PB3' | 'PB4' | 'PB5':
            return DDRB
        case _:
            raise NotImplementedError('Unsupported Pin')

@inline
def select_pin(name: str) -> ptr[uint8]:
    match name:
        case 'PB0' | 'PB1' | 'PB2' | 'PB3' | 'PB4' | 'PB5':
            return PINB
        case _:
            raise NotImplementedError('Unsupported Pin')

@inline
def select_bit(name: str) -> uint8:
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
