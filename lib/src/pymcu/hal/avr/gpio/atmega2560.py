# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
#
# ATmega2560 GPIO HAL
#
# Ports A-G (data 0x20-0x34): use SBI/CBI/IN/OUT where accessible via I/O
# Ports H, J, K, L (data 0x100-0x10B): extended I/O -- require LDS/STS
#
# External interrupts: INT0-INT3 on PD0-PD3 (EICRA), INT4-INT7 on PE4-PE7 (EICRB)
# Interrupt vectors (byte offsets):
#   INT0=0x0002, INT1=0x0004, INT2=0x0006, INT3=0x0008
#   INT4=0x000A, INT5=0x000C, INT6=0x000E, INT7=0x0010
# -----------------------------------------------------------------------------

from pymcu.chips.atmega2560 import (
    PINA, DDRA, PORTA,
    PINB, DDRB, PORTB,
    PINC, DDRC, PORTC,
    PIND, DDRD, PORTD,
    PINE, DDRE, PORTE,
    PINF, DDRF, PORTF,
    PING, DDRG, PORTG,
    PINH, DDRH, PORTH,
    PINJ, DDRJ, PORTJ,
    PINK, DDRK, PORTK,
    PINL, DDRL, PORTL,
    EICRA, EICRB, EIMSK, SREG
)
from pymcu.types import uint8, uint16, inline, ptr, compile_isr, const

@inline
def select_port(name: str) -> ptr[uint8]:
    match name:
        case 'PA0' | 'PA1' | 'PA2' | 'PA3' | 'PA4' | 'PA5' | 'PA6' | 'PA7':
            return PORTA
        case 'PB0' | 'PB1' | 'PB2' | 'PB3' | 'PB4' | 'PB5' | 'PB6' | 'PB7':
            return PORTB
        case 'PC0' | 'PC1' | 'PC2' | 'PC3' | 'PC4' | 'PC5' | 'PC6' | 'PC7':
            return PORTC
        case 'PD0' | 'PD1' | 'PD2' | 'PD3' | 'PD4' | 'PD5' | 'PD6' | 'PD7':
            return PORTD
        case 'PE0' | 'PE1' | 'PE2' | 'PE3' | 'PE4' | 'PE5' | 'PE6' | 'PE7':
            return PORTE
        case 'PF0' | 'PF1' | 'PF2' | 'PF3' | 'PF4' | 'PF5' | 'PF6' | 'PF7':
            return PORTF
        case 'PG0' | 'PG1' | 'PG2' | 'PG3' | 'PG4':
            return PORTG
        case 'PH0' | 'PH1' | 'PH2' | 'PH3' | 'PH4' | 'PH5' | 'PH6' | 'PH7':
            return PORTH
        case 'PJ0' | 'PJ1' | 'PJ2' | 'PJ3' | 'PJ4' | 'PJ5' | 'PJ6' | 'PJ7':
            return PORTJ
        case 'PK0' | 'PK1' | 'PK2' | 'PK3' | 'PK4' | 'PK5' | 'PK6' | 'PK7':
            return PORTK
        case 'PL0' | 'PL1' | 'PL2' | 'PL3' | 'PL4' | 'PL5' | 'PL6' | 'PL7':
            return PORTL
        case _:
            raise NotImplementedError('Unsupported Pin')

@inline
def select_ddr(name: str) -> ptr[uint8]:
    match name:
        case 'PA0' | 'PA1' | 'PA2' | 'PA3' | 'PA4' | 'PA5' | 'PA6' | 'PA7':
            return DDRA
        case 'PB0' | 'PB1' | 'PB2' | 'PB3' | 'PB4' | 'PB5' | 'PB6' | 'PB7':
            return DDRB
        case 'PC0' | 'PC1' | 'PC2' | 'PC3' | 'PC4' | 'PC5' | 'PC6' | 'PC7':
            return DDRC
        case 'PD0' | 'PD1' | 'PD2' | 'PD3' | 'PD4' | 'PD5' | 'PD6' | 'PD7':
            return DDRD
        case 'PE0' | 'PE1' | 'PE2' | 'PE3' | 'PE4' | 'PE5' | 'PE6' | 'PE7':
            return DDRE
        case 'PF0' | 'PF1' | 'PF2' | 'PF3' | 'PF4' | 'PF5' | 'PF6' | 'PF7':
            return DDRF
        case 'PG0' | 'PG1' | 'PG2' | 'PG3' | 'PG4':
            return DDRG
        case 'PH0' | 'PH1' | 'PH2' | 'PH3' | 'PH4' | 'PH5' | 'PH6' | 'PH7':
            return DDRH
        case 'PJ0' | 'PJ1' | 'PJ2' | 'PJ3' | 'PJ4' | 'PJ5' | 'PJ6' | 'PJ7':
            return DDRJ
        case 'PK0' | 'PK1' | 'PK2' | 'PK3' | 'PK4' | 'PK5' | 'PK6' | 'PK7':
            return DDRK
        case 'PL0' | 'PL1' | 'PL2' | 'PL3' | 'PL4' | 'PL5' | 'PL6' | 'PL7':
            return DDRL
        case _:
            raise NotImplementedError('Unsupported Pin')

@inline
def select_pin(name: str) -> ptr[uint8]:
    match name:
        case 'PA0' | 'PA1' | 'PA2' | 'PA3' | 'PA4' | 'PA5' | 'PA6' | 'PA7':
            return PINA
        case 'PB0' | 'PB1' | 'PB2' | 'PB3' | 'PB4' | 'PB5' | 'PB6' | 'PB7':
            return PINB
        case 'PC0' | 'PC1' | 'PC2' | 'PC3' | 'PC4' | 'PC5' | 'PC6' | 'PC7':
            return PINC
        case 'PD0' | 'PD1' | 'PD2' | 'PD3' | 'PD4' | 'PD5' | 'PD6' | 'PD7':
            return PIND
        case 'PE0' | 'PE1' | 'PE2' | 'PE3' | 'PE4' | 'PE5' | 'PE6' | 'PE7':
            return PINE
        case 'PF0' | 'PF1' | 'PF2' | 'PF3' | 'PF4' | 'PF5' | 'PF6' | 'PF7':
            return PINF
        case 'PG0' | 'PG1' | 'PG2' | 'PG3' | 'PG4':
            return PING
        case 'PH0' | 'PH1' | 'PH2' | 'PH3' | 'PH4' | 'PH5' | 'PH6' | 'PH7':
            return PINH
        case 'PJ0' | 'PJ1' | 'PJ2' | 'PJ3' | 'PJ4' | 'PJ5' | 'PJ6' | 'PJ7':
            return PINJ
        case 'PK0' | 'PK1' | 'PK2' | 'PK3' | 'PK4' | 'PK5' | 'PK6' | 'PK7':
            return PINK
        case 'PL0' | 'PL1' | 'PL2' | 'PL3' | 'PL4' | 'PL5' | 'PL6' | 'PL7':
            return PINL
        case _:
            raise NotImplementedError('Unsupported Pin')

@inline
def select_bit(name: str) -> uint8:
    match name:
        case 'PA0' | 'PB0' | 'PC0' | 'PD0' | 'PE0' | 'PF0' | 'PG0' | 'PH0' | 'PJ0' | 'PK0' | 'PL0':
            return 0
        case 'PA1' | 'PB1' | 'PC1' | 'PD1' | 'PE1' | 'PF1' | 'PG1' | 'PH1' | 'PJ1' | 'PK1' | 'PL1':
            return 1
        case 'PA2' | 'PB2' | 'PC2' | 'PD2' | 'PE2' | 'PF2' | 'PG2' | 'PH2' | 'PJ2' | 'PK2' | 'PL2':
            return 2
        case 'PA3' | 'PB3' | 'PC3' | 'PD3' | 'PE3' | 'PF3' | 'PG3' | 'PH3' | 'PJ3' | 'PK3' | 'PL3':
            return 3
        case 'PA4' | 'PB4' | 'PC4' | 'PD4' | 'PE4' | 'PF4' | 'PG4' | 'PH4' | 'PJ4' | 'PK4' | 'PL4':
            return 4
        case 'PA5' | 'PB5' | 'PC5' | 'PD5' | 'PE5' | 'PF5' | 'PH5' | 'PJ5' | 'PK5' | 'PL5':
            return 5
        case 'PA6' | 'PB6' | 'PC6' | 'PD6' | 'PE6' | 'PF6' | 'PH6' | 'PJ6' | 'PK6' | 'PL6':
            return 6
        case 'PA7' | 'PB7' | 'PC7' | 'PD7' | 'PE7' | 'PF7' | 'PH7' | 'PJ7' | 'PK7' | 'PL7':
            return 7
        case _:
            raise NotImplementedError('Unsupported Pin')

@inline
def pin_irq_setup(name: str, trigger: uint8, handler: const = 0):
    # trigger: IRQ_FALLING=1, IRQ_RISING=2, IRQ_CHANGE=3, IRQ_LOW_LEVEL=4
    # EICRA/EICRB ISCn1:ISCn0 encoding: 00=low-level, 01=any-edge, 10=falling, 11=rising
    # INT0-INT3 on PD0-PD3 (EICRA), INT4-INT7 on PE4-PE7 (EICRB)
    if name == "PD0":
        # INT0: EICRA bits 1:0
        if trigger == 1:
            EICRA[0] = 0
            EICRA[1] = 1
        elif trigger == 2:
            EICRA[0] = 1
            EICRA[1] = 1
        elif trigger == 3:
            EICRA[0] = 1
            EICRA[1] = 0
        elif trigger == 4:
            EICRA[0] = 0
            EICRA[1] = 0
        EIMSK[0] = 1
        SREG[7] = 1
        compile_isr(handler, 0x0002)
    elif name == "PD1":
        # INT1: EICRA bits 3:2
        if trigger == 1:
            EICRA[2] = 0
            EICRA[3] = 1
        elif trigger == 2:
            EICRA[2] = 1
            EICRA[3] = 1
        elif trigger == 3:
            EICRA[2] = 1
            EICRA[3] = 0
        elif trigger == 4:
            EICRA[2] = 0
            EICRA[3] = 0
        EIMSK[1] = 1
        SREG[7] = 1
        compile_isr(handler, 0x0004)
    elif name == "PD2":
        # INT2: EICRA bits 5:4
        if trigger == 1:
            EICRA[4] = 0
            EICRA[5] = 1
        elif trigger == 2:
            EICRA[4] = 1
            EICRA[5] = 1
        elif trigger == 3:
            EICRA[4] = 1
            EICRA[5] = 0
        elif trigger == 4:
            EICRA[4] = 0
            EICRA[5] = 0
        EIMSK[2] = 1
        SREG[7] = 1
        compile_isr(handler, 0x0006)
    elif name == "PD3":
        # INT3: EICRA bits 7:6
        if trigger == 1:
            EICRA[6] = 0
            EICRA[7] = 1
        elif trigger == 2:
            EICRA[6] = 1
            EICRA[7] = 1
        elif trigger == 3:
            EICRA[6] = 1
            EICRA[7] = 0
        elif trigger == 4:
            EICRA[6] = 0
            EICRA[7] = 0
        EIMSK[3] = 1
        SREG[7] = 1
        compile_isr(handler, 0x0008)
    elif name == "PE4":
        # INT4: EICRB bits 1:0
        if trigger == 1:
            EICRB[0] = 0
            EICRB[1] = 1
        elif trigger == 2:
            EICRB[0] = 1
            EICRB[1] = 1
        elif trigger == 3:
            EICRB[0] = 1
            EICRB[1] = 0
        elif trigger == 4:
            EICRB[0] = 0
            EICRB[1] = 0
        EIMSK[4] = 1
        SREG[7] = 1
        compile_isr(handler, 0x000A)
    elif name == "PE5":
        # INT5: EICRB bits 3:2
        if trigger == 1:
            EICRB[2] = 0
            EICRB[3] = 1
        elif trigger == 2:
            EICRB[2] = 1
            EICRB[3] = 1
        elif trigger == 3:
            EICRB[2] = 1
            EICRB[3] = 0
        elif trigger == 4:
            EICRB[2] = 0
            EICRB[3] = 0
        EIMSK[5] = 1
        SREG[7] = 1
        compile_isr(handler, 0x000C)
    elif name == "PE6":
        # INT6: EICRB bits 5:4
        if trigger == 1:
            EICRB[4] = 0
            EICRB[5] = 1
        elif trigger == 2:
            EICRB[4] = 1
            EICRB[5] = 1
        elif trigger == 3:
            EICRB[4] = 1
            EICRB[5] = 0
        elif trigger == 4:
            EICRB[4] = 0
            EICRB[5] = 0
        EIMSK[6] = 1
        SREG[7] = 1
        compile_isr(handler, 0x000E)
    elif name == "PE7":
        # INT7: EICRB bits 7:6
        if trigger == 1:
            EICRB[6] = 0
            EICRB[7] = 1
        elif trigger == 2:
            EICRB[6] = 1
            EICRB[7] = 1
        elif trigger == 3:
            EICRB[6] = 1
            EICRB[7] = 0
        elif trigger == 4:
            EICRB[6] = 0
            EICRB[7] = 0
        EIMSK[7] = 1
        SREG[7] = 1
        compile_isr(handler, 0x0010)

def pin_pulse_in(pin_reg: ptr[uint8], bit: uint8, state: uint8, timeout_us: uint16) -> uint16:
    # pulse_in is not yet implemented for ATmega2560
    raise NotImplementedError("pulse_in not supported on ATmega2560")
