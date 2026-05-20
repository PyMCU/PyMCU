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
RAM_SIZE = 2560

device_info(chip="atmega32u4", arch="avr", ram_size=RAM_SIZE)

# ==========================================
#  Register Definitions (ATmega32U4)
# ==========================================

# Port B (0x23-0x25) -- I/O range (SBI/CBI/IN/OUT)
PINB:    ptr[uint8] = ptr(0x23)
DDRB:    ptr[uint8] = ptr(0x24)
PORTB:   ptr[uint8] = ptr(0x25)

# Port C (0x26-0x28) -- I/O range
PINC:    ptr[uint8] = ptr(0x26)
DDRC:    ptr[uint8] = ptr(0x27)
PORTC:   ptr[uint8] = ptr(0x28)

# Port D (0x29-0x2B) -- I/O range
PIND:    ptr[uint8] = ptr(0x29)
DDRD:    ptr[uint8] = ptr(0x2A)
PORTD:   ptr[uint8] = ptr(0x2B)

# Port E (0x2C-0x2E) -- I/O range
PINE:    ptr[uint8] = ptr(0x2C)
DDRE:    ptr[uint8] = ptr(0x2D)
PORTE:   ptr[uint8] = ptr(0x2E)

# Port F (0x2F-0x31) -- I/O range (ADC port)
PINF:    ptr[uint8] = ptr(0x2F)
DDRF:    ptr[uint8] = ptr(0x30)
PORTF:   ptr[uint8] = ptr(0x31)

TIFR0:   ptr[uint8] = ptr(0x35)
TIFR1:   ptr[uint8] = ptr(0x36)
TIFR3:   ptr[uint8] = ptr(0x38)
TIFR4:   ptr[uint8] = ptr(0x39)

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
OCDR:    ptr[uint8] = ptr(0x51)

PLLCSR:  ptr[uint8] = ptr(0x52)

SMCR:    ptr[uint8] = ptr(0x53)
MCUSR:   ptr[uint8] = ptr(0x54)
MCUCR:   ptr[uint8] = ptr(0x55)

SPMCSR:  ptr[uint8] = ptr(0x57)

SPL:     ptr[uint8] = ptr(0x5D)
SPH:     ptr[uint8] = ptr(0x5E)
SREG:    ptr[uint8] = ptr(0x5F)

# Watchdog Timer
WDTCSR:  ptr[uint8] = ptr(0x60)

# Clock Prescaler
CLKPR:   ptr[uint8] = ptr(0x61)

# PLL Frequency Control
PLLFRQ:  ptr[uint8] = ptr(0x52)

# Sleep mode / PRR
PRR0:    ptr[uint8] = ptr(0x64)
PRR1:    ptr[uint8] = ptr(0x65)

# External Interrupt Control
EICRA:   ptr[uint8] = ptr(0x69)
EICRB:   ptr[uint8] = ptr(0x6A)

PCMSK0:  ptr[uint8] = ptr(0x6B)
PCICR:   ptr[uint8] = ptr(0x68)

# Timer Interrupt Mask registers
TIMSK0:  ptr[uint8] = ptr(0x6E)
TIMSK1:  ptr[uint8] = ptr(0x6F)
TIMSK3:  ptr[uint8] = ptr(0x71)
TIMSK4:  ptr[uint8] = ptr(0x72)

# ADC registers (same addresses as ATmega328P)
ADCL:    ptr[uint8] = ptr(0x78)
ADCH:    ptr[uint8] = ptr(0x79)
ADCSRA:  ptr[uint8] = ptr(0x7A)
ADCSRB:  ptr[uint8] = ptr(0x7B)
ADMUX:   ptr[uint8] = ptr(0x7C)
DIDR2:   ptr[uint8] = ptr(0x7D)
DIDR0:   ptr[uint8] = ptr(0x7E)
DIDR1:   ptr[uint8] = ptr(0x7F)

# Timer 1 (16-bit)
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
OCR1CL:  ptr[uint8] = ptr(0x8C)
OCR1CH:  ptr[uint8] = ptr(0x8D)

TCNT1:   ptr[uint16] = ptr(0x84)
ICR1:    ptr[uint16] = ptr(0x86)
OCR1A:   ptr[uint16] = ptr(0x88)
OCR1B:   ptr[uint16] = ptr(0x8A)
OCR1C:   ptr[uint16] = ptr(0x8C)

# Timer 3 (16-bit) -- at 0x90
TCCR3A:  ptr[uint8] = ptr(0x90)
TCCR3B:  ptr[uint8] = ptr(0x91)
TCCR3C:  ptr[uint8] = ptr(0x92)
TCNT3L:  ptr[uint8] = ptr(0x94)
TCNT3H:  ptr[uint8] = ptr(0x95)
ICR3L:   ptr[uint8] = ptr(0x96)
ICR3H:   ptr[uint8] = ptr(0x97)
OCR3AL:  ptr[uint8] = ptr(0x98)
OCR3AH:  ptr[uint8] = ptr(0x99)
OCR3BL:  ptr[uint8] = ptr(0x9A)
OCR3BH:  ptr[uint8] = ptr(0x9B)
OCR3CL:  ptr[uint8] = ptr(0x9C)
OCR3CH:  ptr[uint8] = ptr(0x9D)

TCNT3:   ptr[uint16] = ptr(0x94)
ICR3:    ptr[uint16] = ptr(0x96)
OCR3A:   ptr[uint16] = ptr(0x98)
OCR3B:   ptr[uint16] = ptr(0x9A)
OCR3C:   ptr[uint16] = ptr(0x9C)

# USART1 (not USART0)
UCSR1A:  ptr[uint8] = ptr(0xC8)
UCSR1B:  ptr[uint8] = ptr(0xC9)
UCSR1C:  ptr[uint8] = ptr(0xCA)
UBRR1L:  ptr[uint8] = ptr(0xCC)
UBRR1H:  ptr[uint8] = ptr(0xCD)
UDR1:    ptr[uint8] = ptr(0xCE)

# Timer 4 (10-bit high speed) -- at 0xC0
TCCR4A:  ptr[uint8] = ptr(0xC0)
TCCR4B:  ptr[uint8] = ptr(0xC1)
TCCR4C:  ptr[uint8] = ptr(0xC2)
TCCR4D:  ptr[uint8] = ptr(0xC3)
TCCR4E:  ptr[uint8] = ptr(0xC4)
CLKSEL0: ptr[uint8] = ptr(0xC5)
CLKSEL1: ptr[uint8] = ptr(0xC6)
CLKSTA:  ptr[uint8] = ptr(0xC7)
TCNT4:   ptr[uint8] = ptr(0xBE)
TC4H:    ptr[uint8] = ptr(0xBF)
OCR4A:   ptr[uint8] = ptr(0xCF)
OCR4B:   ptr[uint8] = ptr(0xD0)
OCR4C:   ptr[uint8] = ptr(0xD1)
OCR4D:   ptr[uint8] = ptr(0xD2)
DT4:     ptr[uint8] = ptr(0xD4)

# USB Controller registers (informational -- not used by HAL yet)
UHWCON:  ptr[uint8] = ptr(0xD7)
USBCON:  ptr[uint8] = ptr(0xD8)
USBSTA:  ptr[uint8] = ptr(0xD9)
USBINT:  ptr[uint8] = ptr(0xDA)

# ==========================================
#  Bit Definitions
# ==========================================

# Port B
PORTB7: int = 7; PORTB6: int = 6; PORTB5: int = 5; PORTB4: int = 4
PORTB3: int = 3; PORTB2: int = 2; PORTB1: int = 1; PORTB0: int = 0

# Port C
PORTC7: int = 7; PORTC6: int = 6

# Port D
PORTD7: int = 7; PORTD6: int = 6; PORTD5: int = 5; PORTD4: int = 4
PORTD3: int = 3; PORTD2: int = 2; PORTD1: int = 1; PORTD0: int = 0

# Port E
PORTE6: int = 6; PORTE2: int = 2

# Port F
PORTF7: int = 7; PORTF6: int = 6; PORTF5: int = 5; PORTF4: int = 4
PORTF1: int = 1; PORTF0: int = 0

# Status Register bits
I: int = 7; T: int = 6; H: int = 5; S: int = 4
V: int = 3; N: int = 2; Z: int = 1; C: int = 0
