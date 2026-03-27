# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
#
# ATtiny84 -- 14-pin AVR, PORTA (PA0-PA7) + PORTB (PB0-PB3, PB3=RESET)
# Also covers ATtiny44 and ATtiny24 (same pinout, less flash/SRAM).
# 512B SRAM (ATtiny84), 8KB flash, 512B EEPROM
# Note: ATtiny84 does NOT have a hardware multiplier (AVRe core).
#       Code using Python multiplication (*) requires a software fallback.
# -----------------------------------------------------------------------------

from pymcu.types import ptr, uint8, uint16, device_info

# ==========================================
#  Device Memory Configuration
# ==========================================
RAM_START = 0x0060
RAM_SIZE = 512

device_info(chip="attiny84", arch="avr", ram_size=RAM_SIZE)

# ==========================================
#  Register Definitions (ATtiny84)
# ==========================================

# GPIO -- PORTB (DATA addresses; I/O base + 0x20)
PINB:   ptr[uint8] = ptr(0x36)   # I/O 0x16
DDRB:   ptr[uint8] = ptr(0x37)   # I/O 0x17
PORTB:  ptr[uint8] = ptr(0x38)   # I/O 0x18

# GPIO -- PORTA (DATA addresses; I/O base + 0x20)
PINA:   ptr[uint8] = ptr(0x39)   # I/O 0x19
DDRA:   ptr[uint8] = ptr(0x3A)   # I/O 0x1A
PORTA:  ptr[uint8] = ptr(0x3B)   # I/O 0x1B

# ADC
ADCL:   ptr[uint8] = ptr(0x20)   # I/O 0x00
ADCH:   ptr[uint8] = ptr(0x21)   # I/O 0x01
ADCSRA: ptr[uint8] = ptr(0x22)   # I/O 0x02
ADCSRB: ptr[uint8] = ptr(0x23)   # I/O 0x03
ADMUX:  ptr[uint8] = ptr(0x24)   # I/O 0x04

# ADC 16-bit access
ADC:    ptr[uint16] = ptr(0x20)

# Analog Comparator
ACSR:   ptr[uint8] = ptr(0x25)   # I/O 0x05

# USI (Universal Serial Interface -- bit-bang SPI/I2C)
USICR:  ptr[uint8] = ptr(0x2D)   # I/O 0x0D
USISR:  ptr[uint8] = ptr(0x2E)   # I/O 0x0E
USIDR:  ptr[uint8] = ptr(0x2F)   # I/O 0x0F

# EEPROM
EECR:   ptr[uint8] = ptr(0x3C)   # I/O 0x1C
EEDR:   ptr[uint8] = ptr(0x3D)   # I/O 0x1D
EEAR:   ptr[uint8] = ptr(0x3E)   # I/O 0x1E

# Power Reduction
PRR:    ptr[uint8] = ptr(0x40)   # I/O 0x20

# Watchdog
WDTCSR: ptr[uint8] = ptr(0x41)   # I/O 0x21

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
PCMSK0: ptr[uint8] = ptr(0x32)   # I/O 0x12  (PORTB pins)
PCMSK1: ptr[uint8] = ptr(0x40)   # I/O 0x20  (PORTA pins, shared with PRR on some sub-variants)

# General Interrupt Mask / Flag
GIMSK:  ptr[uint8] = ptr(0x5B)   # I/O 0x3B
GIFR:   ptr[uint8] = ptr(0x5A)   # I/O 0x3A

# Timer/Counter 0 (8-bit)
OCR0B:  ptr[uint8] = ptr(0x3C)   # I/O 0x1C
OCR0A:  ptr[uint8] = ptr(0x3D)   # I/O 0x1D
TCCR0A: ptr[uint8] = ptr(0x4A)   # I/O 0x2A
TCNT0:  ptr[uint8] = ptr(0x52)   # I/O 0x32
TCCR0B: ptr[uint8] = ptr(0x53)   # I/O 0x33

# Timer/Counter 1 (16-bit)
ICR1L:  ptr[uint8] = ptr(0x44)   # I/O 0x24
ICR1H:  ptr[uint8] = ptr(0x45)   # I/O 0x25
OCR1BL: ptr[uint8] = ptr(0x48)   # I/O 0x28
OCR1BH: ptr[uint8] = ptr(0x49)   # I/O 0x29
OCR1AL: ptr[uint8] = ptr(0x4A)   # I/O 0x2A
OCR1AH: ptr[uint8] = ptr(0x4B)   # I/O 0x2B
TCNT1L: ptr[uint8] = ptr(0x4C)   # I/O 0x2C
TCNT1H: ptr[uint8] = ptr(0x4D)   # I/O 0x2D
TCCR1A: ptr[uint8] = ptr(0x4F)   # I/O 0x2F
TCCR1B: ptr[uint8] = ptr(0x50)   # I/O 0x30
TCCR1C: ptr[uint8] = ptr(0x51)   # I/O 0x31

# Timer Interrupt Mask / Flag
TIMSK0: ptr[uint8] = ptr(0x59)   # I/O 0x39
TIMSK1: ptr[uint8] = ptr(0x5C)   # I/O 0x3C
TIFR0:  ptr[uint8] = ptr(0x58)   # I/O 0x38
TIFR1:  ptr[uint8] = ptr(0x5B)   # I/O 0x3B

# ==========================================
#  Bit Definitions
# ==========================================

# Port A
PORTA7: int = 7; PORTA6: int = 6; PORTA5: int = 5; PORTA4: int = 4
PORTA3: int = 3; PORTA2: int = 2; PORTA1: int = 1; PORTA0: int = 0

DDA7: int = 7; DDA6: int = 6; DDA5: int = 5; DDA4: int = 4
DDA3: int = 3; DDA2: int = 2; DDA1: int = 1; DDA0: int = 0

PINA7: int = 7; PINA6: int = 6; PINA5: int = 5; PINA4: int = 4
PINA3: int = 3; PINA2: int = 2; PINA1: int = 1; PINA0: int = 0

# Port B
PORTB3: int = 3; PORTB2: int = 2; PORTB1: int = 1; PORTB0: int = 0

DDB3: int = 3; DDB2: int = 2; DDB1: int = 1; DDB0: int = 0

PINB3: int = 3; PINB2: int = 2; PINB1: int = 1; PINB0: int = 0

# Status Register
I: int = 7; T: int = 6; H: int = 5; S: int = 4
V: int = 3; N: int = 2; Z: int = 1; C: int = 0

# ADCSRA bits
ADEN:  int = 7; ADSC:  int = 6; ADATE: int = 5; ADIF: int = 4
ADIE:  int = 3; ADPS2: int = 2; ADPS1: int = 1; ADPS0: int = 0

# ADMUX bits
REFS1: int = 7; REFS0: int = 6; ADLAR: int = 5
MUX5:  int = 5; MUX4:  int = 4; MUX3:  int = 3
MUX2:  int = 2; MUX1:  int = 1; MUX0:  int = 0

# GIMSK bits
INT0:  int = 6; PCIE1: int = 5; PCIE0: int = 4

# WDTCSR bits
WDIF: int = 7; WDIE: int = 6; WDP3: int = 5; WDCE: int = 4
WDE:  int = 3; WDP2: int = 2; WDP1: int = 1; WDP0: int = 0
