# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
#
# ATtiny13 -- 8-pin AVR, PORTB only (PB0-PB5 usable, PB5=RESET)
# 64B SRAM, 1KB flash, 64B EEPROM. Very minimal chip.
# Note: ATtiny13 does NOT have a hardware multiplier (AVRe core).
#       Code using Python multiplication (*) requires a software fallback.
# -----------------------------------------------------------------------------

from pymcu.types import ptr, uint8, uint16, device_info

# ==========================================
#  Device Memory Configuration
# ==========================================
RAM_START = 0x0060
RAM_SIZE = 64

device_info(chip="attiny13", arch="avr", ram_size=RAM_SIZE)

# ==========================================
#  Register Definitions (ATtiny13)
# ==========================================

# GPIO -- PORTB only (DATA addresses; I/O base + 0x20)
PINB:   ptr[uint8] = ptr(0x36)   # I/O 0x16
DDRB:   ptr[uint8] = ptr(0x37)   # I/O 0x17
PORTB:  ptr[uint8] = ptr(0x38)   # I/O 0x18

# ADC (no ADCSRB on ATtiny13)
ADCL:   ptr[uint8] = ptr(0x20)   # I/O 0x00
ADCH:   ptr[uint8] = ptr(0x21)   # I/O 0x01
ADCSRA: ptr[uint8] = ptr(0x22)   # I/O 0x02
ADMUX:  ptr[uint8] = ptr(0x24)   # I/O 0x04

# ADC 16-bit access
ADC:    ptr[uint16] = ptr(0x20)

# Analog Comparator
ACSR:   ptr[uint8] = ptr(0x25)   # I/O 0x05

# EEPROM
EECR:   ptr[uint8] = ptr(0x3C)   # I/O 0x1C
EEDR:   ptr[uint8] = ptr(0x3D)   # I/O 0x1D
EEAR:   ptr[uint8] = ptr(0x3E)   # I/O 0x1E

# Watchdog
WDTCR:  ptr[uint8] = ptr(0x41)   # I/O 0x21

# Clock Prescaler
CLKPR:  ptr[uint8] = ptr(0x46)   # I/O 0x26

# Oscillator Calibration
OSCCAL: ptr[uint8] = ptr(0x51)   # I/O 0x31

# Stack Pointer & Status
SPL:    ptr[uint8] = ptr(0x5D)   # I/O 0x3D
SREG:   ptr[uint8] = ptr(0x5F)   # I/O 0x3F

# MCU Control
MCUSR:  ptr[uint8] = ptr(0x54)   # I/O 0x34
MCUCR:  ptr[uint8] = ptr(0x55)   # I/O 0x35

# Pin Change Interrupt
PCMSK:  ptr[uint8] = ptr(0x35)   # I/O 0x15

# General Interrupt Mask / Flag
GIMSK:  ptr[uint8] = ptr(0x5B)   # I/O 0x3B
GIFR:   ptr[uint8] = ptr(0x5A)   # I/O 0x3A

# Timer/Counter 0 (8-bit)
OCR0B:  ptr[uint8] = ptr(0x48)   # I/O 0x28
OCR0A:  ptr[uint8] = ptr(0x49)   # I/O 0x29
TCCR0A: ptr[uint8] = ptr(0x4A)   # I/O 0x2A
TCNT0:  ptr[uint8] = ptr(0x52)   # I/O 0x32
TCCR0B: ptr[uint8] = ptr(0x53)   # I/O 0x33

# Timer Interrupt Mask / Flag
TIMSK0: ptr[uint8] = ptr(0x59)   # I/O 0x39
TIFR0:  ptr[uint8] = ptr(0x58)   # I/O 0x38

# ==========================================
#  Bit Definitions
# ==========================================

# Port B
PORTB5: int = 5; PORTB4: int = 4; PORTB3: int = 3
PORTB2: int = 2; PORTB1: int = 1; PORTB0: int = 0

DDB5: int = 5; DDB4: int = 4; DDB3: int = 3
DDB2: int = 2; DDB1: int = 1; DDB0: int = 0

PINB5: int = 5; PINB4: int = 4; PINB3: int = 3
PINB2: int = 2; PINB1: int = 1; PINB0: int = 0

# Status Register
I: int = 7; T: int = 6; H: int = 5; S: int = 4
V: int = 3; N: int = 2; Z: int = 1; C: int = 0

# ADCSRA bits
ADEN:  int = 7; ADSC:  int = 6; ADATE: int = 5; ADIF: int = 4
ADIE:  int = 3; ADPS2: int = 2; ADPS1: int = 1; ADPS0: int = 0

# ADMUX bits
REFS0: int = 6; ADLAR: int = 5
MUX1:  int = 1; MUX0:  int = 0

# TIMSK0 / TIFR0 bits
OCIE0B: int = 2; OCIE0A: int = 1; TOIE0: int = 0

# TCCR0A bits
COM0A1: int = 7; COM0A0: int = 6; COM0B1: int = 5; COM0B0: int = 4
WGM01:  int = 1; WGM00:  int = 0

# TCCR0B bits
FOC0A: int = 7; FOC0B: int = 6; WGM02: int = 3
CS02:  int = 2; CS01:  int = 1; CS00:  int = 0

# GIMSK bits
INT0:  int = 6; PCIE:  int = 5

# MCUCR bits
SM1: int = 4; SM0: int = 3; SE: int = 5; ISC01: int = 1; ISC00: int = 0

# WDTCR bits
WDIF: int = 7; WDIE: int = 6; WDP3: int = 5; WDCE: int = 4
WDE:  int = 3; WDP2: int = 2; WDP1: int = 1; WDP0: int = 0
