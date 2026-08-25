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
from pymcu.exceptions import CompileError

# `name` is a port name ('PB7') or an Arduino Mega board number (13) throughout this
# module. Both spellings share one match per lookup so the number folds away exactly
# like the name does. D0-D53 are the Mega's digital pins and 54-69 are A0-A15.
@inline
def select_port(name: str) -> ptr[uint8]:
    match name:
        case 'PA0' | 'PA1' | 'PA2' | 'PA3' | 'PA4' | 'PA5' | 'PA6' | 'PA7' | 22 | 23 | 24 | 25 | 26 | 27 | 28 | 29:
            return PORTA
        case 'PB0' | 'PB1' | 'PB2' | 'PB3' | 'PB4' | 'PB5' | 'PB6' | 'PB7' | 10 | 11 | 12 | 13 | 50 | 51 | 52 | 53:
            return PORTB
        case 'PC0' | 'PC1' | 'PC2' | 'PC3' | 'PC4' | 'PC5' | 'PC6' | 'PC7' | 30 | 31 | 32 | 33 | 34 | 35 | 36 | 37:
            return PORTC
        case 'PD0' | 'PD1' | 'PD2' | 'PD3' | 'PD4' | 'PD5' | 'PD6' | 'PD7' | 18 | 19 | 20 | 21 | 38:
            return PORTD
        case 'PE0' | 'PE1' | 'PE2' | 'PE3' | 'PE4' | 'PE5' | 'PE6' | 'PE7' | 0 | 1 | 2 | 3 | 5:
            return PORTE
        case 'PF0' | 'PF1' | 'PF2' | 'PF3' | 'PF4' | 'PF5' | 'PF6' | 'PF7' | 54 | 55 | 56 | 57 | 58 | 59 | 60 | 61:
            return PORTF
        case 'PG0' | 'PG1' | 'PG2' | 'PG3' | 'PG4' | 39 | 40 | 41:
            return PORTG
        case 'PH0' | 'PH1' | 'PH2' | 'PH3' | 'PH4' | 'PH5' | 'PH6' | 'PH7' | 6 | 7 | 8 | 9 | 16 | 17:
            return PORTH
        case 'PJ0' | 'PJ1' | 'PJ2' | 'PJ3' | 'PJ4' | 'PJ5' | 'PJ6' | 'PJ7' | 14 | 15:
            return PORTJ
        case 'PK0' | 'PK1' | 'PK2' | 'PK3' | 'PK4' | 'PK5' | 'PK6' | 'PK7' | 62 | 63 | 64 | 65 | 66 | 67 | 68 | 69:
            return PORTK
        case 'PL0' | 'PL1' | 'PL2' | 'PL3' | 'PL4' | 'PL5' | 'PL6' | 'PL7' | 42 | 43 | 44 | 45 | 46 | 47 | 48 | 49:
            return PORTL
        case _:
            raise CompileError(
                "unknown pin on this chip. Give a PORT NAME (PA0-PA7, PB0-PB7, "
                "PC0-PC7, PD0-PD7, PE0-PE7, PF0-PF7, PG0-PG4, PH0-PH7, PJ0-PJ7, "
                "PK0-PK7, PL0-PL7) or an Arduino Mega board number (0-69; 13 is "
                "the built-in LED and 54-69 are A0-A15).")

@inline
def select_ddr(name: str) -> ptr[uint8]:
    match name:
        case 'PA0' | 'PA1' | 'PA2' | 'PA3' | 'PA4' | 'PA5' | 'PA6' | 'PA7' | 22 | 23 | 24 | 25 | 26 | 27 | 28 | 29:
            return DDRA
        case 'PB0' | 'PB1' | 'PB2' | 'PB3' | 'PB4' | 'PB5' | 'PB6' | 'PB7' | 10 | 11 | 12 | 13 | 50 | 51 | 52 | 53:
            return DDRB
        case 'PC0' | 'PC1' | 'PC2' | 'PC3' | 'PC4' | 'PC5' | 'PC6' | 'PC7' | 30 | 31 | 32 | 33 | 34 | 35 | 36 | 37:
            return DDRC
        case 'PD0' | 'PD1' | 'PD2' | 'PD3' | 'PD4' | 'PD5' | 'PD6' | 'PD7' | 18 | 19 | 20 | 21 | 38:
            return DDRD
        case 'PE0' | 'PE1' | 'PE2' | 'PE3' | 'PE4' | 'PE5' | 'PE6' | 'PE7' | 0 | 1 | 2 | 3 | 5:
            return DDRE
        case 'PF0' | 'PF1' | 'PF2' | 'PF3' | 'PF4' | 'PF5' | 'PF6' | 'PF7' | 54 | 55 | 56 | 57 | 58 | 59 | 60 | 61:
            return DDRF
        case 'PG0' | 'PG1' | 'PG2' | 'PG3' | 'PG4' | 39 | 40 | 41:
            return DDRG
        case 'PH0' | 'PH1' | 'PH2' | 'PH3' | 'PH4' | 'PH5' | 'PH6' | 'PH7' | 6 | 7 | 8 | 9 | 16 | 17:
            return DDRH
        case 'PJ0' | 'PJ1' | 'PJ2' | 'PJ3' | 'PJ4' | 'PJ5' | 'PJ6' | 'PJ7' | 14 | 15:
            return DDRJ
        case 'PK0' | 'PK1' | 'PK2' | 'PK3' | 'PK4' | 'PK5' | 'PK6' | 'PK7' | 62 | 63 | 64 | 65 | 66 | 67 | 68 | 69:
            return DDRK
        case 'PL0' | 'PL1' | 'PL2' | 'PL3' | 'PL4' | 'PL5' | 'PL6' | 'PL7' | 42 | 43 | 44 | 45 | 46 | 47 | 48 | 49:
            return DDRL
        case _:
            raise CompileError(
                "unknown pin on this chip. Give a PORT NAME (PA0-PA7, PB0-PB7, "
                "PC0-PC7, PD0-PD7, PE0-PE7, PF0-PF7, PG0-PG4, PH0-PH7, PJ0-PJ7, "
                "PK0-PK7, PL0-PL7) or an Arduino Mega board number (0-69; 13 is "
                "the built-in LED and 54-69 are A0-A15).")

@inline
def select_pin(name: str) -> ptr[uint8]:
    match name:
        case 'PA0' | 'PA1' | 'PA2' | 'PA3' | 'PA4' | 'PA5' | 'PA6' | 'PA7' | 22 | 23 | 24 | 25 | 26 | 27 | 28 | 29:
            return PINA
        case 'PB0' | 'PB1' | 'PB2' | 'PB3' | 'PB4' | 'PB5' | 'PB6' | 'PB7' | 10 | 11 | 12 | 13 | 50 | 51 | 52 | 53:
            return PINB
        case 'PC0' | 'PC1' | 'PC2' | 'PC3' | 'PC4' | 'PC5' | 'PC6' | 'PC7' | 30 | 31 | 32 | 33 | 34 | 35 | 36 | 37:
            return PINC
        case 'PD0' | 'PD1' | 'PD2' | 'PD3' | 'PD4' | 'PD5' | 'PD6' | 'PD7' | 18 | 19 | 20 | 21 | 38:
            return PIND
        case 'PE0' | 'PE1' | 'PE2' | 'PE3' | 'PE4' | 'PE5' | 'PE6' | 'PE7' | 0 | 1 | 2 | 3 | 5:
            return PINE
        case 'PF0' | 'PF1' | 'PF2' | 'PF3' | 'PF4' | 'PF5' | 'PF6' | 'PF7' | 54 | 55 | 56 | 57 | 58 | 59 | 60 | 61:
            return PINF
        case 'PG0' | 'PG1' | 'PG2' | 'PG3' | 'PG4' | 39 | 40 | 41:
            return PING
        case 'PH0' | 'PH1' | 'PH2' | 'PH3' | 'PH4' | 'PH5' | 'PH6' | 'PH7' | 6 | 7 | 8 | 9 | 16 | 17:
            return PINH
        case 'PJ0' | 'PJ1' | 'PJ2' | 'PJ3' | 'PJ4' | 'PJ5' | 'PJ6' | 'PJ7' | 14 | 15:
            return PINJ
        case 'PK0' | 'PK1' | 'PK2' | 'PK3' | 'PK4' | 'PK5' | 'PK6' | 'PK7' | 62 | 63 | 64 | 65 | 66 | 67 | 68 | 69:
            return PINK
        case 'PL0' | 'PL1' | 'PL2' | 'PL3' | 'PL4' | 'PL5' | 'PL6' | 'PL7' | 42 | 43 | 44 | 45 | 46 | 47 | 48 | 49:
            return PINL
        case _:
            raise CompileError(
                "unknown pin on this chip. Give a PORT NAME (PA0-PA7, PB0-PB7, "
                "PC0-PC7, PD0-PD7, PE0-PE7, PF0-PF7, PG0-PG4, PH0-PH7, PJ0-PJ7, "
                "PK0-PK7, PL0-PL7) or an Arduino Mega board number (0-69; 13 is "
                "the built-in LED and 54-69 are A0-A15).")

@inline
def select_bit(name: str) -> uint8:
    match name:
        case 'PA0' | 'PB0' | 'PC0' | 'PD0' | 'PE0' | 'PF0' | 'PG0' | 'PH0' | 'PJ0' | 'PK0' | 'PL0' | 0 | 15 | 17 | 21 | 22 | 37 | 41 | 49 | 53 | 54 | 62:
            return 0
        case 'PA1' | 'PB1' | 'PC1' | 'PD1' | 'PE1' | 'PF1' | 'PG1' | 'PH1' | 'PJ1' | 'PK1' | 'PL1' | 1 | 14 | 16 | 20 | 23 | 36 | 40 | 48 | 52 | 55 | 63:
            return 1
        case 'PA2' | 'PB2' | 'PC2' | 'PD2' | 'PE2' | 'PF2' | 'PG2' | 'PH2' | 'PJ2' | 'PK2' | 'PL2' | 19 | 24 | 35 | 39 | 47 | 51 | 56 | 64:
            return 2
        case 'PA3' | 'PB3' | 'PC3' | 'PD3' | 'PE3' | 'PF3' | 'PG3' | 'PH3' | 'PJ3' | 'PK3' | 'PL3' | 5 | 6 | 18 | 25 | 34 | 46 | 50 | 57 | 65:
            return 3
        case 'PA4' | 'PB4' | 'PC4' | 'PD4' | 'PE4' | 'PF4' | 'PG4' | 'PH4' | 'PJ4' | 'PK4' | 'PL4' | 2 | 7 | 10 | 26 | 33 | 45 | 58 | 66:
            return 4
        case 'PA5' | 'PB5' | 'PC5' | 'PD5' | 'PE5' | 'PF5' | 'PH5' | 'PJ5' | 'PK5' | 'PL5' | 3 | 8 | 11 | 27 | 32 | 44 | 59 | 67:
            return 5
        case 'PA6' | 'PB6' | 'PC6' | 'PD6' | 'PE6' | 'PF6' | 'PH6' | 'PJ6' | 'PK6' | 'PL6' | 9 | 12 | 28 | 31 | 43 | 60 | 68:
            return 6
        case 'PA7' | 'PB7' | 'PC7' | 'PD7' | 'PE7' | 'PF7' | 'PH7' | 'PJ7' | 'PK7' | 'PL7' | 13 | 29 | 30 | 38 | 42 | 61 | 69:
            return 7
        case _:
            raise CompileError(
                "unknown pin on this chip. Give a PORT NAME (PA0-PA7, PB0-PB7, "
                "PC0-PC7, PD0-PD7, PE0-PE7, PF0-PF7, PG0-PG4, PH0-PH7, PJ0-PJ7, "
                "PK0-PK7, PL0-PL7) or an Arduino Mega board number (0-69; 13 is "
                "the built-in LED and 54-69 are A0-A15).")

@inline
def pin_irq_setup(name: str, trigger: uint8, handler: const = 0):
    # trigger: IRQ_FALLING=1, IRQ_RISING=2, IRQ_CHANGE=3, IRQ_LOW_LEVEL=4
    # EICRA/EICRB ISCn1:ISCn0 encoding: 00=low-level, 01=any-edge, 10=falling, 11=rising
    # INT0-INT3 on PD0-PD3 (EICRA), INT4-INT7 on PE4-PE7 (EICRB)
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

    # A port name ('PD0') or a Mega board number (21) -- the same two spellings
    # Pin() takes, matched together so both fold to one branch.
    match name:
        case 'PD0' | 21:
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
        case 'PD1' | 20:
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
        case 'PD2' | 19:
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
        case 'PD3' | 18:
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
        case 'PE4' | 2:
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
        case 'PE5' | 3:
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
        case 'PE6':
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
        case 'PE7':
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
