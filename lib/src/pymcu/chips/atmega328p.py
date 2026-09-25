# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------

from pymcu.types import ptr, uint8, uint16, device_info

# ==========================================
#  Device Memory Configuration
# ==========================================
RAM_START = 0x0100
RAM_SIZE = 2048
FLASH_SIZE = 32768

device_info(chip="atmega328p", arch="avr", ram_size=RAM_SIZE, flash_size=FLASH_SIZE)

# ==========================================
#  Register Definitions (ATmega328P)
# ==========================================

# I/O Registers (Memory Mapped Address)
PINB:    ptr[uint8] = ptr(0x23)
DDRB:    ptr[uint8] = ptr(0x24)
PORTB:   ptr[uint8] = ptr(0x25)

PINC:    ptr[uint8] = ptr(0x26)
DDRC:    ptr[uint8] = ptr(0x27)
PORTC:   ptr[uint8] = ptr(0x28)

PIND:    ptr[uint8] = ptr(0x29)
DDRD:    ptr[uint8] = ptr(0x2A)
PORTD:   ptr[uint8] = ptr(0x2B)

TIFR0:   ptr[uint8] = ptr(0x35)
TIFR1:   ptr[uint8] = ptr(0x36)
TIFR2:   ptr[uint8] = ptr(0x37)

PCIFR:   ptr[uint8] = ptr(0x3B)
EIFR:    ptr[uint8] = ptr(0x3C)
EIMSK:   ptr[uint8] = ptr(0x3D)
GPIOR0:  ptr[uint8] = ptr(0x3E)
EECR:    ptr[uint8] = ptr(0x3F)
EEDR:    ptr[uint8] = ptr(0x40)
EEARL:   ptr[uint8] = ptr(0x41)
EEARH:   ptr[uint8] = ptr(0x42)
GTCCR:   ptr[uint8] = ptr(0x43)
TCCR0A:  ptr[uint8] = ptr(0x44)
TCCR0B:  ptr[uint8] = ptr(0x45)
TCNT0:   ptr[uint8] = ptr(0x46)
OCR0A:   ptr[uint8] = ptr(0x47)
OCR0B:   ptr[uint8] = ptr(0x48)

GPIOR1:  ptr[uint8] = ptr(0x4A)
GPIOR2:  ptr[uint8] = ptr(0x4B)
SPCR:    ptr[uint8] = ptr(0x4C)
SPSR:    ptr[uint8] = ptr(0x4D)
SPDR:    ptr[uint8] = ptr(0x4E)

ACSR:    ptr[uint8] = ptr(0x50)
SMCR:    ptr[uint8] = ptr(0x53)
MCUSR:   ptr[uint8] = ptr(0x54)
MCUCR:   ptr[uint8] = ptr(0x55)
SPMCSR:  ptr[uint8] = ptr(0x57)

SPL:     ptr[uint8] = ptr(0x5D)
SPH:     ptr[uint8] = ptr(0x5E)
SREG:    ptr[uint8] = ptr(0x5F)

WDTCSR:  ptr[uint8] = ptr(0x60)
CLKPR:   ptr[uint8] = ptr(0x61)
PRR:     ptr[uint8] = ptr(0x64)
OSCCAL:  ptr[uint8] = ptr(0x66)
PCICR:   ptr[uint8] = ptr(0x68)
EICRA:   ptr[uint8] = ptr(0x69)
PCMSK0:  ptr[uint8] = ptr(0x6B)
PCMSK1:  ptr[uint8] = ptr(0x6C)
PCMSK2:  ptr[uint8] = ptr(0x6D)
TIMSK0:  ptr[uint8] = ptr(0x6E)
TIMSK1:  ptr[uint8] = ptr(0x6F)
TIMSK2:  ptr[uint8] = ptr(0x70)

ADCL:    ptr[uint8] = ptr(0x78)
ADCH:    ptr[uint8] = ptr(0x79)
ADCSRA:  ptr[uint8] = ptr(0x7A)
ADCSRB:  ptr[uint8] = ptr(0x7B)
ADMUX:   ptr[uint8] = ptr(0x7C)
DIDR0:   ptr[uint8] = ptr(0x7E)
DIDR1:   ptr[uint8] = ptr(0x7F)

TCCR1A:  ptr[uint8] = ptr(0x80)
TCCR1B:  ptr[uint8] = ptr(0x81)
TCCR1C:  ptr[uint8] = ptr(0x82)
TCNT1L:  ptr[uint8] = ptr(0x84)
TCNT1H:  ptr[uint8] = ptr(0x85)
ICR1L:   ptr[uint8] = ptr(0x86)
ICR1H:   ptr[uint8] = ptr(0x87)
OCR1AL:  ptr[uint8] = ptr(0x88)
OCR1AH:  ptr[uint8] = ptr(0x89)
OCR1BL:  ptr[uint8] = ptr(0x8A)
OCR1BH:  ptr[uint8] = ptr(0x8B)

# 16-bit Register Access
TCNT1:   ptr[uint16] = ptr(0x84)
ICR1:    ptr[uint16] = ptr(0x86)
OCR1A:   ptr[uint16] = ptr(0x88)
OCR1B:   ptr[uint16] = ptr(0x8A)

TCCR2A:  ptr[uint8] = ptr(0xB0)
TCCR2B:  ptr[uint8] = ptr(0xB1)
TCNT2:   ptr[uint8] = ptr(0xB2)
OCR2A:   ptr[uint8] = ptr(0xB3)
OCR2B:   ptr[uint8] = ptr(0xB4)
ASSR:    ptr[uint8] = ptr(0xB6)

TWBR:    ptr[uint8] = ptr(0xB8)
TWSR:    ptr[uint8] = ptr(0xB9)
TWAR:    ptr[uint8] = ptr(0xBA)
TWDR:    ptr[uint8] = ptr(0xBB)
TWCR:    ptr[uint8] = ptr(0xBC)
TWAMR:   ptr[uint8] = ptr(0xBD)

# TWI bus pins on this part: SDA is PC4, SCL is PC5. i2c_init raises
# these PORT bits to switch the internal pull-ups on before enabling the
# TWI, the way Arduino's twi_init() does -- a module with weak or missing
# pull-up resistors answers anyway. Numbers, not pin-name strings: a
# string in a module every program imports shifts its string pool.
TWI_SDA_PORT = 0x28
TWI_SDA_BIT = 4
TWI_SCL_PORT = 0x28
TWI_SCL_BIT = 5


UCSR0A:  ptr[uint8] = ptr(0xC0)
UCSR0B:  ptr[uint8] = ptr(0xC1)
UCSR0C:  ptr[uint8] = ptr(0xC2)
UBRR0L:  ptr[uint8] = ptr(0xC4)
UBRR0H:  ptr[uint8] = ptr(0xC5)
UDR0:    ptr[uint8] = ptr(0xC6)

# ==========================================
#  Bit Definitions
# ==========================================

# Port B
PORTB7: int = 7; PORTB6: int = 6; PORTB5: int = 5; PORTB4: int = 4
PORTB3: int = 3; PORTB2: int = 2; PORTB1: int = 1; PORTB0: int = 0

DDB7: int = 7; DDB6: int = 6; DDB5: int = 5; DDB4: int = 4
DDB3: int = 3; DDB2: int = 2; DDB1: int = 1; DDB0: int = 0

PINB7: int = 7; PINB6: int = 6; PINB5: int = 5; PINB4: int = 4
PINB3: int = 3; PINB2: int = 2; PINB1: int = 1; PINB0: int = 0

# Status Register
I: int = 7; T: int = 6; H: int = 5; S: int = 4
V: int = 3; N: int = 2; Z: int = 1; C: int = 0


# ==========================================
#  Grouped Peripherals (stable surface)
# ==========================================
#
# The module-level register names above are how the HAL reaches the silicon, and
# they are an implementation detail: the set, the spelling and the widths follow
# whatever the HAL needs and change with it. A program that writes TCCR1B by that
# name is writing against a surface the project does not promise.
#
# A grouped peripheral is the promise. Every register of one peripheral is an
# attribute of a class named after it, the way an XC8 program reaches T1CON
# through the Timer1 SFR block, and the class is what the documentation covers:
#
#   from pymcu.chips.atmega328p import Timer1
#
#   Timer1.TCCR1A.value = 0x82                  # whole register
#   Timer1.ICR1.value = 19999                   # 16-bit, one name
#   Timer1.TCCR1B[Timer1.CS10] = 1              # one bit, by its datasheet name
#   if Timer1.TIFR1[Timer1.TOV1]:
#       Timer1.TIFR1[Timer1.TOV1] = 1           # write 1 to clear
#
# Costs nothing: a class-level ptr declaration is a register, so every access
# compiles to the same LDS/STS/SBI/CBI as the loose name and the class itself has
# no runtime existence. The addresses are the loose names, not copies of them, so
# the two spellings cannot drift apart.
#
# Register names are the datasheet's. Bit positions are members of the same class
# so one import brings the whole peripheral and no bit name reaches module scope.
#
# Naming follows PEP 8. The group is a class, so it is CapWords (Timer1, not TIMER1);
# the loose names above are module-level constants, so they keep ALL_CAPS; and the
# registers inside the group keep ALL_CAPS too, which is what makes the group a pure
# regrouping of the loose list. A group is a namespace and not a type, so calling it
# (Timer1()) is a compile error that says so.


class Timer1:
    """Timer/Counter1 of the ATmega328P: 16-bit, two compare units, input capture."""

    # Control and status
    TCCR1A: ptr[uint8] = ptr(TCCR1A)
    TCCR1B: ptr[uint8] = ptr(TCCR1B)
    TCCR1C: ptr[uint8] = ptr(TCCR1C)

    # Counter and compare/capture, 16-bit
    TCNT1: ptr[uint16] = ptr(TCNT1)
    OCR1A: ptr[uint16] = ptr(OCR1A)
    OCR1B: ptr[uint16] = ptr(OCR1B)
    ICR1: ptr[uint16] = ptr(ICR1)

    # The byte halves of the same four registers. The 16-bit names above are the
    # ones to use: the AVR latches the high byte through a shared temporary
    # register, and the compiler orders the halves for you.
    TCNT1L: ptr[uint8] = ptr(TCNT1L)
    TCNT1H: ptr[uint8] = ptr(TCNT1H)
    OCR1AL: ptr[uint8] = ptr(OCR1AL)
    OCR1AH: ptr[uint8] = ptr(OCR1AH)
    OCR1BL: ptr[uint8] = ptr(OCR1BL)
    OCR1BH: ptr[uint8] = ptr(OCR1BH)
    ICR1L: ptr[uint8] = ptr(ICR1L)
    ICR1H: ptr[uint8] = ptr(ICR1H)

    # Interrupt mask and flags
    TIMSK1: ptr[uint8] = ptr(TIMSK1)
    TIFR1: ptr[uint8] = ptr(TIFR1)

    # Prescaler control, shared with Timer/Counter0 and Timer/Counter2
    GTCCR: ptr[uint8] = ptr(GTCCR)

    # TCCR1A
    COM1A1: int = 7; COM1A0: int = 6
    COM1B1: int = 5; COM1B0: int = 4
    WGM11: int = 1; WGM10: int = 0

    # TCCR1B
    ICNC1: int = 7; ICES1: int = 6
    WGM13: int = 4; WGM12: int = 3
    CS12: int = 2; CS11: int = 1; CS10: int = 0

    # TCCR1C
    FOC1A: int = 7; FOC1B: int = 6

    # TIMSK1
    ICIE1: int = 5; OCIE1B: int = 2; OCIE1A: int = 1; TOIE1: int = 0

    # TIFR1 -- a flag is cleared by writing a ONE to it
    ICF1: int = 5; OCF1B: int = 2; OCF1A: int = 1; TOV1: int = 0
