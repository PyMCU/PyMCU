# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
#
# ATmega32U4 GPIO HAL
#
# Ports B/C/D/E/F (all I/O range 0x23-0x31): SBI/CBI/IN/OUT accessible.
# External interrupts: INT0-INT3 on PD0-PD3 (EICRA), INT4-INT7 on PE4/PE5/PE6/PE7 (n/a on 32U4 -- only INT4-INT6 exist)
# Interrupt vectors (byte offsets):
#   INT0=0x0002, INT1=0x0004, INT2=0x0006, INT3=0x0008
#   INT4=0x000A, INT5=0x000C, INT6=0x000E
# -----------------------------------------------------------------------------

from pymcu.chips.atmega32u4 import (
    PINB, DDRB, PORTB,
    PINC, DDRC, PORTC,
    PIND, DDRD, PORTD,
    PINE, DDRE, PORTE,
    PINF, DDRF, PORTF,
    EICRA, EICRB, EIMSK, SREG
)
from pymcu.types import uint8, uint16, inline, ptr, compile_isr, const
from pymcu.exceptions import CompileError

@inline
def select_port(name: str) -> ptr[uint8]:
    match name:
        case 'PB0' | 'PB1' | 'PB2' | 'PB3' | 'PB4' | 'PB5' | 'PB6' | 'PB7':
            return PORTB
        case 'PC6' | 'PC7':
            return PORTC
        case 'PD0' | 'PD1' | 'PD2' | 'PD3' | 'PD4' | 'PD5' | 'PD6' | 'PD7':
            return PORTD
        case 'PE2' | 'PE6':
            return PORTE
        case 'PF0' | 'PF1' | 'PF4' | 'PF5' | 'PF6' | 'PF7':
            return PORTF
        case _:
            raise NotImplementedError('Unsupported Pin')

@inline
def select_ddr(name: str) -> ptr[uint8]:
    match name:
        case 'PB0' | 'PB1' | 'PB2' | 'PB3' | 'PB4' | 'PB5' | 'PB6' | 'PB7':
            return DDRB
        case 'PC6' | 'PC7':
            return DDRC
        case 'PD0' | 'PD1' | 'PD2' | 'PD3' | 'PD4' | 'PD5' | 'PD6' | 'PD7':
            return DDRD
        case 'PE2' | 'PE6':
            return DDRE
        case 'PF0' | 'PF1' | 'PF4' | 'PF5' | 'PF6' | 'PF7':
            return DDRF
        case _:
            raise NotImplementedError('Unsupported Pin')

@inline
def select_pin(name: str) -> ptr[uint8]:
    match name:
        case 'PB0' | 'PB1' | 'PB2' | 'PB3' | 'PB4' | 'PB5' | 'PB6' | 'PB7':
            return PINB
        case 'PC6' | 'PC7':
            return PINC
        case 'PD0' | 'PD1' | 'PD2' | 'PD3' | 'PD4' | 'PD5' | 'PD6' | 'PD7':
            return PIND
        case 'PE2' | 'PE6':
            return PINE
        case 'PF0' | 'PF1' | 'PF4' | 'PF5' | 'PF6' | 'PF7':
            return PINF
        case _:
            raise NotImplementedError('Unsupported Pin')

@inline
def select_bit(name: str) -> uint8:
    match name:
        case 'PB0' | 'PC0' | 'PD0' | 'PE0' | 'PF0':
            return 0
        case 'PB1' | 'PC1' | 'PD1' | 'PE1' | 'PF1':
            return 1
        case 'PB2' | 'PC2' | 'PD2' | 'PE2' | 'PF2':
            return 2
        case 'PB3' | 'PC3' | 'PD3' | 'PE3' | 'PF3':
            return 3
        case 'PB4' | 'PC4' | 'PD4' | 'PE4' | 'PF4':
            return 4
        case 'PB5' | 'PC5' | 'PD5' | 'PE5' | 'PF5':
            return 5
        case 'PB6' | 'PC6' | 'PD6' | 'PE6' | 'PF6':
            return 6
        case 'PB7' | 'PC7' | 'PD7' | 'PE7' | 'PF7':
            return 7
        case _:
            raise NotImplementedError('Unsupported Pin')

@inline
def pin_irq_setup(name: str, trigger: uint8, handler: const = 0):
    # trigger: IRQ_FALLING=1, IRQ_RISING=2, IRQ_CHANGE=3, IRQ_LOW_LEVEL=4
    # EICRA ISCn1:ISCn0 encoding: 00=low-level, 01=any-edge, 10=falling, 11=rising
    # INT0-INT3 on PD0-PD3 (EICRA)
    # INT4-INT6 on PE4/PE5/PE6 (EICRB) -- INT7 not available on 32U4
    #
    # Checked BEFORE any register is touched, and once for every pin: a trigger that
    # matched no arm below used to fall off the end of the if/elif chain with the ISCn
    # bits left at their reset value 0 -- which is LOW LEVEL -- and EIMSK enabled anyway.
    # The pin the user asked about never fired, and its complement re-asserted the
    # interrupt for as long as it stayed low, which wedges the part in an ISR that never
    # returns.
    if trigger == 8:
        raise CompileError(
            "Pin.IRQ_HIGH_LEVEL is not supported on this chip. The external interrupts "
            "encode only four triggers in ISCn1:ISCn0 -- low level, any edge, falling and "
            "rising -- and high level is not one of them. Use Pin.IRQ_RISING for the "
            "moment the pin goes high, or read the pin in your loop.")
    if trigger != 1 and trigger != 2 and trigger != 3 and trigger != 4:
        raise CompileError(
            "unknown irq trigger. Pin.irq() takes ONE of Pin.IRQ_FALLING, Pin.IRQ_RISING, "
            "Pin.IRQ_CHANGE or Pin.IRQ_LOW_LEVEL. The four are not a bit mask that can be "
            "combined freely: `Pin.IRQ_FALLING | Pin.IRQ_RISING` is 3, which is exactly "
            "Pin.IRQ_CHANGE, and no other combination names a trigger the hardware has.")

    if name == "PD0":
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
    else:
        raise NotImplementedError('Pin IRQ not supported on ATmega32U4 for this pin')

def pin_pulse_in(pin: ptr[uint8], bit: uint8, state: uint8, timeout_us: uint16) -> uint16:
    raise NotImplementedError('pulse_in not yet implemented on ATmega32U4')
