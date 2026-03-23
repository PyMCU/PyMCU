# -----------------------------------------------------------------------------
# Whipsnake Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the Whipsnake Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
#
# ATtiny2313 -- 20-pin AVR, PORTD (PD0-PD6) + PORTB (PB0-PB7)
# Also covers ATtiny4313 (same pinout, 4KB flash instead of 2KB).
# 128B SRAM (ATtiny2313) / 256B SRAM (ATtiny4313), no ADC, no EEPROM on 2313A
# Note: ATtiny2313 does NOT have a hardware multiplier (AVRe core).
#       Code using Python multiplication (*) requires a software fallback.
# -----------------------------------------------------------------------------

from whipsnake.types import ptr, uint8, uint16, device_info

# ==========================================
#  Device Memory Configuration
# ==========================================
RAM_START = 0x0060
RAM_SIZE = 128

device_info(chip="attiny2313", arch="avr", ram_size=RAM_SIZE)

# ==========================================
#  Register Definitions (ATtiny2313)
# ==========================================

# GPIO -- PORTD (DATA addresses; I/O base + 0x20)
PIND:   ptr[uint8] = ptr(0x30)   # I/O 0x10
DDRD:   ptr[uint8] = ptr(0x31)   # I/O 0x11
PORTD:  ptr[uint8] = ptr(0x32)   # I/O 0x12

# GPIO -- PORTB (DATA addresses; I/O base + 0x20)
PINB:   ptr[uint8] = ptr(0x36)   # I/O 0x16
DDRB:   ptr[uint8] = ptr(0x37)   # I/O 0x17
PORTB:  ptr[uint8] = ptr(0x38)   # I/O 0x18

# USI (Universal Serial Interface -- bit-bang SPI/I2C)
USICR:  ptr[uint8] = ptr(0x2D)   # I/O 0x0D
USISR:  ptr[uint8] = ptr(0x2E)   # I/O 0x0E
USIDR:  ptr[uint8] = ptr(0x2F)   # I/O 0x0F

# UART (USART)
UDR:    ptr[uint8] = ptr(0x2C)   # I/O 0x0C
UCSRA:  ptr[uint8] = ptr(0x2B)   # I/O 0x0B  (UCSR0A)
UCSRB:  ptr[uint8] = ptr(0x2A)   # I/O 0x0A  (UCSR0B)
UCSRC:  ptr[uint8] = ptr(0x43)   # I/O 0x23  (UCSR0C)
UBRRL:  ptr[uint8] = ptr(0x29)   # I/O 0x09  (UBRR0L)
UBRRH:  ptr[uint8] = ptr(0x42)   # I/O 0x22  (UBRR0H)

# EEPROM
EECR:   ptr[uint8] = ptr(0x3C)   # I/O 0x1C
EEDR:   ptr[uint8] = ptr(0x3D)   # I/O 0x1D
EEAR:   ptr[uint8] = ptr(0x3E)   # I/O 0x1E

# Watchdog
WDTCSR: ptr[uint8] = ptr(0x41)   # I/O 0x21

# Stack Pointer & Status
SPL:    ptr[uint8] = ptr(0x5D)   # I/O 0x3D
SREG:   ptr[uint8] = ptr(0x5F)   # I/O 0x3F

# MCU Control
MCUSR:  ptr[uint8] = ptr(0x54)   # I/O 0x34
MCUCR:  ptr[uint8] = ptr(0x55)   # I/O 0x35

# General Interrupt Mask / Flag
GIMSK:  ptr[uint8] = ptr(0x5B)   # I/O 0x3B
GIFR:   ptr[uint8] = ptr(0x5A)   # I/O 0x3A

# Pin Change Interrupt
PCMSK:  ptr[uint8] = ptr(0x40)   # I/O 0x20

# Timer/Counter 0 (8-bit)
OCR0A:  ptr[uint8] = ptr(0x49)   # I/O 0x29
OCR0B:  ptr[uint8] = ptr(0x48)   # I/O 0x28
TCCR0A: ptr[uint8] = ptr(0x4A)   # I/O 0x2A
TCCR0B: ptr[uint8] = ptr(0x53)   # I/O 0x33
TCNT0:  ptr[uint8] = ptr(0x52)   # I/O 0x32

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
TIMSK:  ptr[uint8] = ptr(0x59)   # I/O 0x39
TIFR:   ptr[uint8] = ptr(0x58)   # I/O 0x38

# ==========================================
#  Bit Definitions
# ==========================================

# Port D
PORTD6: int = 6; PORTD5: int = 5; PORTD4: int = 4; PORTD3: int = 3
PORTD2: int = 2; PORTD1: int = 1; PORTD0: int = 0

DDD6: int = 6; DDD5: int = 5; DDD4: int = 4; DDD3: int = 3
DDD2: int = 2; DDD1: int = 1; DDD0: int = 0

PIND6: int = 6; PIND5: int = 5; PIND4: int = 4; PIND3: int = 3
PIND2: int = 2; PIND1: int = 1; PIND0: int = 0

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

# GIMSK bits
INT1:  int = 7; INT0:  int = 6; PCIE:  int = 5

# UCSRA bits
RXC:  int = 7; TXC:  int = 6; UDRE: int = 5; FE:   int = 4
DOR:  int = 3; UPE:  int = 2; U2X:  int = 1; MPCM: int = 0

# UCSRB bits
RXCIE: int = 7; TXCIE: int = 6; UDRIE: int = 5; RXEN: int = 4
TXEN:  int = 3; UCSZ2: int = 2; RXB8:  int = 1; TXB8: int = 0

# WDTCSR bits
WDIF: int = 7; WDIE: int = 6; WDP3: int = 5; WDCE: int = 4
WDE:  int = 3; WDP2: int = 2; WDP1: int = 1; WDP0: int = 0
