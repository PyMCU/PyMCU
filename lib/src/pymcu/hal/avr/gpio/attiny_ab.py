# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
#
# ATtiny GPIO HAL -- dual-port chips (ATtiny84/44/24)
#
# GPIO is on PORTA (PA0-PA7) and PORTB (PB0-PB3, PB3=RESET).
#
# DATA addresses (I/O address + 0x20):
#   PINB  = 0x36  (I/O 0x16)
#   DDRB  = 0x37  (I/O 0x17)
#   PORTB = 0x38  (I/O 0x18)
#   PINA  = 0x39  (I/O 0x19)
#   DDRA  = 0x3A  (I/O 0x1A)
#   PORTA = 0x3B  (I/O 0x1B)
# -----------------------------------------------------------------------------

from pymcu.chips.attiny84 import DDRA, DDRB, PORTA, PORTB, PINA, PINB
from pymcu.types import uint8, uint16, inline, ptr, const

@inline
def select_port(name: str) -> ptr[uint8]:
    match name:
        case 'PA0' | 'PA1' | 'PA2' | 'PA3' | 'PA4' | 'PA5' | 'PA6' | 'PA7':
            return PORTA
        case 'PB0' | 'PB1' | 'PB2' | 'PB3':
            return PORTB
        case _:
            raise NotImplementedError('Unsupported Pin')

@inline
def select_ddr(name: str) -> ptr[uint8]:
    match name:
        case 'PA0' | 'PA1' | 'PA2' | 'PA3' | 'PA4' | 'PA5' | 'PA6' | 'PA7':
            return DDRA
        case 'PB0' | 'PB1' | 'PB2' | 'PB3':
            return DDRB
        case _:
            raise NotImplementedError('Unsupported Pin')

@inline
def select_pin(name: str) -> ptr[uint8]:
    match name:
        case 'PA0' | 'PA1' | 'PA2' | 'PA3' | 'PA4' | 'PA5' | 'PA6' | 'PA7':
            return PINA
        case 'PB0' | 'PB1' | 'PB2' | 'PB3':
            return PINB
        case _:
            raise NotImplementedError('Unsupported Pin')

@inline
def select_bit(name: str) -> uint8:
    match name:
        case 'PA0' | 'PB0':
            return 0
        case 'PA1' | 'PB1':
            return 1
        case 'PA2' | 'PB2':
            return 2
        case 'PA3' | 'PB3':
            return 3
        case 'PA4':
            return 4
        case 'PA5':
            return 5
        case 'PA6':
            return 6
        case 'PA7':
            return 7
        case _:
            raise NotImplementedError('Unsupported Pin')
