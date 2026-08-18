# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
#
# ATmega2560 -- 100-pin AVR, 256KB Flash, 8KB SRAM, 4KB EEPROM
# Arduino Mega 2560 target.
# Ports: A-G (I/O 0x00-0x34) + H, J, K, L (extended I/O, LDS/STS)
# 6 Timers: Timer0 (8-bit), Timer1/3/4/5 (16-bit), Timer2 (8-bit async)
# 4 USART ports, SPI, TWI, 16-channel ADC (PORTF + PORTK)
# -----------------------------------------------------------------------------

from pymcu.types import ptr, uint8, uint16, device_info

# ==========================================
#  Device Memory Configuration
# ==========================================
RAM_START = 0x0200
RAM_SIZE = 8192
FLASH_SIZE = 262144

device_info(chip="atmega2560", arch="avr", ram_size=RAM_SIZE, flash_size=FLASH_SIZE)

# ==========================================
#  Register Definitions (ATmega2560)
# ==========================================

# GPIO -- PORTA (data addresses; I/O + 0x20)
PINA:    ptr[uint8] = ptr(0x20)   # I/O 0x00
DDRA:    ptr[uint8] = ptr(0x21)   # I/O 0x01
PORTA:   ptr[uint8] = ptr(0x22)   # I/O 0x02

# GPIO -- PORTB
PINB:    ptr[uint8] = ptr(0x23)   # I/O 0x03
DDRB:    ptr[uint8] = ptr(0x24)   # I/O 0x04
PORTB:   ptr[uint8] = ptr(0x25)   # I/O 0x05

# GPIO -- PORTC
PINC:    ptr[uint8] = ptr(0x26)   # I/O 0x06
DDRC:    ptr[uint8] = ptr(0x27)   # I/O 0x07
PORTC:   ptr[uint8] = ptr(0x28)   # I/O 0x08

# GPIO -- PORTD
PIND:    ptr[uint8] = ptr(0x29)   # I/O 0x09
DDRD:    ptr[uint8] = ptr(0x2A)   # I/O 0x0A
PORTD:   ptr[uint8] = ptr(0x2B)   # I/O 0x0B

# GPIO -- PORTE
PINE:    ptr[uint8] = ptr(0x2C)   # I/O 0x0C
DDRE:    ptr[uint8] = ptr(0x2D)   # I/O 0x0D
PORTE:   ptr[uint8] = ptr(0x2E)   # I/O 0x0E

# GPIO -- PORTF (also ADC0-ADC7 pins)
PINF:    ptr[uint8] = ptr(0x2F)   # I/O 0x0F
DDRF:    ptr[uint8] = ptr(0x30)   # I/O 0x10
PORTF:   ptr[uint8] = ptr(0x31)   # I/O 0x11

# GPIO -- PORTG (only PG0-PG4 are physical pins on ATmega2560)
PING:    ptr[uint8] = ptr(0x32)   # I/O 0x12
DDRG:    ptr[uint8] = ptr(0x33)   # I/O 0x13
PORTG:   ptr[uint8] = ptr(0x34)   # I/O 0x14

# Timer Interrupt Flag Registers (I/O 0x15-0x1A)
TIFR0:   ptr[uint8] = ptr(0x35)   # I/O 0x15
TIFR1:   ptr[uint8] = ptr(0x36)   # I/O 0x16
TIFR2:   ptr[uint8] = ptr(0x37)   # I/O 0x17
TIFR3:   ptr[uint8] = ptr(0x38)   # I/O 0x18
TIFR4:   ptr[uint8] = ptr(0x39)   # I/O 0x19
TIFR5:   ptr[uint8] = ptr(0x3A)   # I/O 0x1A

# Interrupt Registers
PCIFR:   ptr[uint8] = ptr(0x3B)   # I/O 0x1B
EIFR:    ptr[uint8] = ptr(0x3C)   # I/O 0x1C
EIMSK:   ptr[uint8] = ptr(0x3D)   # I/O 0x1D

# EEPROM
EECR:    ptr[uint8] = ptr(0x3F)   # I/O 0x1F
EEDR:    ptr[uint8] = ptr(0x40)   # I/O 0x20
EEARL:   ptr[uint8] = ptr(0x41)   # I/O 0x21
EEARH:   ptr[uint8] = ptr(0x42)   # I/O 0x22

# Timer 0 (8-bit)
GTCCR:   ptr[uint8] = ptr(0x43)   # I/O 0x23
TCCR0A:  ptr[uint8] = ptr(0x44)   # I/O 0x24
TCCR0B:  ptr[uint8] = ptr(0x45)   # I/O 0x25
TCNT0:   ptr[uint8] = ptr(0x46)   # I/O 0x26
OCR0A:   ptr[uint8] = ptr(0x47)   # I/O 0x27
OCR0B:   ptr[uint8] = ptr(0x48)   # I/O 0x28

# SPI
SPCR:    ptr[uint8] = ptr(0x4C)   # I/O 0x2C
SPSR:    ptr[uint8] = ptr(0x4D)   # I/O 0x2D
SPDR:    ptr[uint8] = ptr(0x4E)   # I/O 0x2E

# MCU Control
SMCR:    ptr[uint8] = ptr(0x53)   # I/O 0x33
MCUSR:   ptr[uint8] = ptr(0x54)   # I/O 0x34
MCUCR:   ptr[uint8] = ptr(0x55)   # I/O 0x35

# Stack Pointer & Status
SPL:     ptr[uint8] = ptr(0x5D)   # I/O 0x3D
SPH:     ptr[uint8] = ptr(0x5E)   # I/O 0x3E
SREG:    ptr[uint8] = ptr(0x5F)   # I/O 0x3F

# Extended I/O (data > 0x5F, requires LDS/STS)
WDTCSR:  ptr[uint8] = ptr(0x60)
CLKPR:   ptr[uint8] = ptr(0x61)
PRR0:    ptr[uint8] = ptr(0x64)
PRR1:    ptr[uint8] = ptr(0x65)
OSCCAL:  ptr[uint8] = ptr(0x66)
PCICR:   ptr[uint8] = ptr(0x68)
EICRA:   ptr[uint8] = ptr(0x69)   # External interrupt control A (INT0-INT3)
EICRB:   ptr[uint8] = ptr(0x6A)   # External interrupt control B (INT4-INT7)
PCMSK0:  ptr[uint8] = ptr(0x6B)
TIMSK0:  ptr[uint8] = ptr(0x6E)
TIMSK1:  ptr[uint8] = ptr(0x6F)
TIMSK2:  ptr[uint8] = ptr(0x70)
TIMSK3:  ptr[uint8] = ptr(0x71)
TIMSK4:  ptr[uint8] = ptr(0x72)
TIMSK5:  ptr[uint8] = ptr(0x73)

# ADC (16-channel: ADC0-ADC7 on PORTF, ADC8-ADC15 on PORTK via MUX5)
ADCL:    ptr[uint8] = ptr(0x78)
ADCH:    ptr[uint8] = ptr(0x79)
ADCSRA:  ptr[uint8] = ptr(0x7A)
ADCSRB:  ptr[uint8] = ptr(0x7B)
ADMUX:   ptr[uint8] = ptr(0x7C)
DIDR2:   ptr[uint8] = ptr(0x7D)
DIDR0:   ptr[uint8] = ptr(0x7E)
DIDR1:   ptr[uint8] = ptr(0x7F)

# Timer 1 (16-bit) -- same addresses as ATmega328P
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

# Timer 3 (16-bit)
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

# Timer 4 (16-bit)
TCCR4A:  ptr[uint8] = ptr(0xA0)
TCCR4B:  ptr[uint8] = ptr(0xA1)
TCCR4C:  ptr[uint8] = ptr(0xA2)
TCNT4L:  ptr[uint8] = ptr(0xA4)
TCNT4H:  ptr[uint8] = ptr(0xA5)
ICR4L:   ptr[uint8] = ptr(0xA6)
ICR4H:   ptr[uint8] = ptr(0xA7)
OCR4AL:  ptr[uint8] = ptr(0xA8)
OCR4AH:  ptr[uint8] = ptr(0xA9)
OCR4BL:  ptr[uint8] = ptr(0xAA)
OCR4BH:  ptr[uint8] = ptr(0xAB)
OCR4CL:  ptr[uint8] = ptr(0xAC)
OCR4CH:  ptr[uint8] = ptr(0xAD)

TCNT4:   ptr[uint16] = ptr(0xA4)
ICR4:    ptr[uint16] = ptr(0xA6)
OCR4A:   ptr[uint16] = ptr(0xA8)
OCR4B:   ptr[uint16] = ptr(0xAA)
OCR4C:   ptr[uint16] = ptr(0xAC)

# Timer 2 (8-bit async) -- same addresses as ATmega328P
TCCR2A:  ptr[uint8] = ptr(0xB0)
TCCR2B:  ptr[uint8] = ptr(0xB1)
TCNT2:   ptr[uint8] = ptr(0xB2)
OCR2A:   ptr[uint8] = ptr(0xB3)
OCR2B:   ptr[uint8] = ptr(0xB4)
ASSR:    ptr[uint8] = ptr(0xB6)

# TWI (I2C) -- same addresses as ATmega328P
TWBR:    ptr[uint8] = ptr(0xB8)
TWSR:    ptr[uint8] = ptr(0xB9)
TWAR:    ptr[uint8] = ptr(0xBA)
TWDR:    ptr[uint8] = ptr(0xBB)
TWCR:    ptr[uint8] = ptr(0xBC)
TWAMR:   ptr[uint8] = ptr(0xBD)

# USART0 -- same addresses as ATmega328P
UCSR0A:  ptr[uint8] = ptr(0xC0)
UCSR0B:  ptr[uint8] = ptr(0xC1)
UCSR0C:  ptr[uint8] = ptr(0xC2)
UBRR0L:  ptr[uint8] = ptr(0xC4)
UBRR0H:  ptr[uint8] = ptr(0xC5)
UDR0:    ptr[uint8] = ptr(0xC6)

# USART1
UCSR1A:  ptr[uint8] = ptr(0xC8)
UCSR1B:  ptr[uint8] = ptr(0xC9)
UCSR1C:  ptr[uint8] = ptr(0xCA)
UBRR1L:  ptr[uint8] = ptr(0xCC)
UBRR1H:  ptr[uint8] = ptr(0xCD)
UDR1:    ptr[uint8] = ptr(0xCE)

# USART2
UCSR2A:  ptr[uint8] = ptr(0xD0)
UCSR2B:  ptr[uint8] = ptr(0xD1)
UCSR2C:  ptr[uint8] = ptr(0xD2)
UBRR2L:  ptr[uint8] = ptr(0xD4)
UBRR2H:  ptr[uint8] = ptr(0xD5)
UDR2:    ptr[uint8] = ptr(0xD6)

# GPIO -- PORTH (extended I/O; data > 0xFF)
PINH:    ptr[uint8] = ptr(0x100)
DDRH:    ptr[uint8] = ptr(0x101)
PORTH:   ptr[uint8] = ptr(0x102)

# GPIO -- PORTJ
PINJ:    ptr[uint8] = ptr(0x103)
DDRJ:    ptr[uint8] = ptr(0x104)
PORTJ:   ptr[uint8] = ptr(0x105)

# GPIO -- PORTK (also ADC8-ADC15 pins)
PINK:    ptr[uint8] = ptr(0x106)
DDRK:    ptr[uint8] = ptr(0x107)
PORTK:   ptr[uint8] = ptr(0x108)

# GPIO -- PORTL
PINL:    ptr[uint8] = ptr(0x109)
DDRL:    ptr[uint8] = ptr(0x10A)
PORTL:   ptr[uint8] = ptr(0x10B)

# USART3
UCSR3A:  ptr[uint8] = ptr(0x130)
UCSR3B:  ptr[uint8] = ptr(0x131)
UCSR3C:  ptr[uint8] = ptr(0x132)
UBRR3L:  ptr[uint8] = ptr(0x134)
UBRR3H:  ptr[uint8] = ptr(0x135)
UDR3:    ptr[uint8] = ptr(0x136)

# Timer 5 (16-bit)
TCCR5A:  ptr[uint8] = ptr(0x120)
TCCR5B:  ptr[uint8] = ptr(0x121)
TCCR5C:  ptr[uint8] = ptr(0x122)
TCNT5L:  ptr[uint8] = ptr(0x124)
TCNT5H:  ptr[uint8] = ptr(0x125)
ICR5L:   ptr[uint8] = ptr(0x126)
ICR5H:   ptr[uint8] = ptr(0x127)
OCR5AL:  ptr[uint8] = ptr(0x128)
OCR5AH:  ptr[uint8] = ptr(0x129)
OCR5BL:  ptr[uint8] = ptr(0x12A)
OCR5BH:  ptr[uint8] = ptr(0x12B)
OCR5CL:  ptr[uint8] = ptr(0x12C)
OCR5CH:  ptr[uint8] = ptr(0x12D)

TCNT5:   ptr[uint16] = ptr(0x124)
ICR5:    ptr[uint16] = ptr(0x126)
OCR5A:   ptr[uint16] = ptr(0x128)
OCR5B:   ptr[uint16] = ptr(0x12A)
OCR5C:   ptr[uint16] = ptr(0x12C)

# ==========================================
#  Bit Definitions
# ==========================================

# Status Register
I: int = 7; T: int = 6; H: int = 5; S: int = 4
V: int = 3; N: int = 2; Z: int = 1; C: int = 0

# Port B
PORTB7: int = 7; PORTB6: int = 6; PORTB5: int = 5; PORTB4: int = 4
PORTB3: int = 3; PORTB2: int = 2; PORTB1: int = 1; PORTB0: int = 0

DDB7: int = 7; DDB6: int = 6; DDB5: int = 5; DDB4: int = 4
DDB3: int = 3; DDB2: int = 2; DDB1: int = 1; DDB0: int = 0

PINB7: int = 7; PINB6: int = 6; PINB5: int = 5; PINB4: int = 4
PINB3: int = 3; PINB2: int = 2; PINB1: int = 1; PINB0: int = 0

# WDTCSR bits
WDIF: int = 7; WDIE: int = 6; WDP3: int = 5; WDCE: int = 4
WDE:  int = 3; WDP2: int = 2; WDP1: int = 1; WDP0: int = 0
