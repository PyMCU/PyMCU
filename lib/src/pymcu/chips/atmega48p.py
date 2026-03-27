# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
#
# ATmega48P -- picoPower variant of ATmega48.
# 512 B SRAM, 4 KB flash; identical register map to ATmega328P.
# Note: ATmega48/48P has no bootloader section (no SPM_RDY interrupt vector).
# -----------------------------------------------------------------------------

from pymcu.types import ptr, uint8, uint16, device_info

# ==========================================
#  Device Memory Configuration
# ==========================================
RAM_START = 0x0100
RAM_SIZE = 512

device_info(chip="atmega48p", arch="avr", ram_size=RAM_SIZE)

# ==========================================
#  Register Definitions (ATmega48P)
# ==========================================

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

UCSR0A:  ptr[uint8] = ptr(0xC0)
UCSR0B:  ptr[uint8] = ptr(0xC1)
UCSR0C:  ptr[uint8] = ptr(0xC2)
UBRR0L:  ptr[uint8] = ptr(0xC4)
UBRR0H:  ptr[uint8] = ptr(0xC5)
UDR0:    ptr[uint8] = ptr(0xC6)

# ==========================================
#  Bit Definitions
# ==========================================

PORTB7: int = 7; PORTB6: int = 6; PORTB5: int = 5; PORTB4: int = 4
PORTB3: int = 3; PORTB2: int = 2; PORTB1: int = 1; PORTB0: int = 0

DDB7: int = 7; DDB6: int = 6; DDB5: int = 5; DDB4: int = 4
DDB3: int = 3; DDB2: int = 2; DDB1: int = 1; DDB0: int = 0

PINB7: int = 7; PINB6: int = 6; PINB5: int = 5; PINB4: int = 4
PINB3: int = 3; PINB2: int = 2; PINB1: int = 1; PINB0: int = 0

I: int = 7; T: int = 6; H: int = 5; S: int = 4
V: int = 3; N: int = 2; Z: int = 1; C: int = 0
