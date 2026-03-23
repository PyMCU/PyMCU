# -----------------------------------------------------------------------------
# Whipsnake Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the Whipsnake Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
#
# ATtiny24 -- 14-pin AVR, PORTA (PA0-PA7) + PORTB (PB0-PB3, PB3=RESET)
# 128B SRAM, 2KB flash, 128B EEPROM
# Note: ATtiny24 does NOT have a hardware multiplier (AVRe core).
#       Code using Python multiplication (*) requires a software fallback.
# -----------------------------------------------------------------------------

from whipsnake.types import ptr, uint8, uint16, device_info

# ==========================================
#  Device Memory Configuration
# ==========================================
RAM_START = 0x0060
RAM_SIZE = 128

device_info(chip="attiny24", arch="avr", ram_size=RAM_SIZE)

# ==========================================
#  Register Definitions (ATtiny24)
# ==========================================
# Register map is identical to ATtiny84; only SRAM size differs.

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
PCMSK1: ptr[uint8] = ptr(0x40)   # I/O 0x20  (PORTA pins)

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

# 16-bit Timer1 access
ICR1:   ptr[uint16] = ptr(0x44)
OCR1A:  ptr[uint16] = ptr(0x4A)
OCR1B:  ptr[uint16] = ptr(0x48)
TCNT1:  ptr[uint16] = ptr(0x4C)

# Timer Interrupt Mask / Flag
TIMSK0: ptr[uint8] = ptr(0x59)   # I/O 0x39
TIMSK1: ptr[uint8] = ptr(0x5C)   # I/O 0x3C
TIFR0:  ptr[uint8] = ptr(0x58)   # I/O 0x38
TIFR1:  ptr[uint8] = ptr(0x5B)   # I/O 0x3B

# General Timer/Counter Control
GTCCR:  ptr[uint8] = ptr(0x43)   # I/O 0x23

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

# Port B (PB0-PB3 usable; PB3=RESET)
PORTB3: int = 3; PORTB2: int = 2; PORTB1: int = 1; PORTB0: int = 0
DDB3:   int = 3; DDB2:   int = 2; DDB1:   int = 1; DDB0:   int = 0
PINB3:  int = 3; PINB2:  int = 2; PINB1:  int = 1; PINB0:  int = 0

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

# TIMSK0 / TIFR0 bits
OCIE0B: int = 2; OCIE0A: int = 1; TOIE0: int = 0

# TIMSK1 / TIFR1 bits
ICIE1:  int = 5; OCIE1B: int = 4; OCIE1A: int = 3; TOIE1: int = 2

# TCCR0A bits
COM0A1: int = 7; COM0A0: int = 6; COM0B1: int = 5; COM0B0: int = 4
WGM01:  int = 1; WGM00:  int = 0

# TCCR0B bits
FOC0A: int = 7; FOC0B: int = 6; WGM02: int = 3
CS02:  int = 2; CS01:  int = 1; CS00:  int = 0

# TCCR1A bits
COM1A1: int = 7; COM1A0: int = 6; COM1B1: int = 5; COM1B0: int = 4
WGM11:  int = 1; WGM10:  int = 0

# TCCR1B bits
ICNC1: int = 7; ICES1: int = 6; WGM13: int = 4; WGM12: int = 3
CS12:  int = 2; CS11:  int = 1; CS10:  int = 0

# GIMSK bits
INT0:  int = 6; PCIE1: int = 5; PCIE0: int = 4

# MCUCR bits
SM1: int = 4; SM0: int = 3; SE: int = 5; ISC01: int = 1; ISC00: int = 0

# WDTCSR bits
WDIF: int = 7; WDIE: int = 6; WDP3: int = 5; WDCE: int = 4
WDE:  int = 3; WDP2: int = 2; WDP1: int = 1; WDP0: int = 0
