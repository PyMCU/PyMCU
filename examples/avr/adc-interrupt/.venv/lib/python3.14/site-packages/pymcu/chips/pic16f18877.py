# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------

from pymcu.types import ptr, uint8, uint16, device_info
from pymcu.time import *

# ==========================================
#  Device Memory Configuration
# ==========================================
# PIC16F18877: 4KB SRAM, 32 Banks
# Linear Data Memory starts at 0x2000, but traditional banks are used here.
RAM_START = 0x20
RAM_SIZE = 4096 # Supports Linear Addressing

device_info(chip="pic16f18877", arch="pic14e", ram_size=RAM_SIZE)

# ==========================================
#  Core Registers (Bank 0 - Common)
# ==========================================
INDF0:    ptr[uint8] = ptr(0x0000)
INDF1:    ptr[uint8] = ptr(0x0001)
PCL:      ptr[uint8] = ptr(0x0002)
STATUS:   ptr[uint8] = ptr(0x0003)
FSR0:     ptr[uint16] = ptr(0x0004) # FSR0L + FSR0H
FSR0L:    ptr[uint8] = ptr(0x0004)
FSR0H:    ptr[uint8] = ptr(0x0005)
FSR1:     ptr[uint16] = ptr(0x0006)
BSR:      ptr[uint8] = ptr(0x0008) # Bank Select Register
WREG:     ptr[uint8] = ptr(0x0009)
PCLATH:   ptr[uint8] = ptr(0x000A)
INTCON:   ptr[uint8] = ptr(0x000B)

# ==========================================
#  I/O PORTS (Bank 0)
# ==========================================
# Note: On Enhanced Mid-Range, PORT/TRIS/LAT are usually all in Bank 0
PORTA:    ptr[uint8] = ptr(0x000C)
PORTB:    ptr[uint8] = ptr(0x000D)
PORTC:    ptr[uint8] = ptr(0x000E)
PORTD:    ptr[uint8] = ptr(0x000F)
PORTE:    ptr[uint8] = ptr(0x0010)

TRISA:    ptr[uint8] = ptr(0x0011)
TRISB:    ptr[uint8] = ptr(0x0012)
TRISC:    ptr[uint8] = ptr(0x0013)
TRISD:    ptr[uint8] = ptr(0x0014)
TRISE:    ptr[uint8] = ptr(0x0015)

LATA:     ptr[uint8] = ptr(0x0016)
LATB:     ptr[uint8] = ptr(0x0017)
LATC:     ptr[uint8] = ptr(0x0018)
LATD:     ptr[uint8] = ptr(0x0019)
LATE:     ptr[uint8] = ptr(0x001A)

# ==========================================
#  Analog Select (Bank 62 - High Bank)
# ==========================================
ANSELA:   ptr[uint8] = ptr(0x1F38)
ANSELB:   ptr[uint8] = ptr(0x1F43)
ANSELC:   ptr[uint8] = ptr(0x1F4E)
ANSELD:   ptr[uint8] = ptr(0x1F59)
ANSELE:   ptr[uint8] = ptr(0x1F64)

# ==========================================
#  ADC Module (Bank 1 & 2)
# ==========================================
ADCON0:   ptr[uint8] = ptr(0x0093)
ADCON1:   ptr[uint8] = ptr(0x0094)
ADCON2:   ptr[uint8] = ptr(0x0095)
ADCON3:   ptr[uint8] = ptr(0x0096)
ADSTAT:   ptr[uint8] = ptr(0x0097)
ADCLK:    ptr[uint8] = ptr(0x0098)
ADPCH:    ptr[uint8] = ptr(0x009E) # Channel Select (No longer in ADCON0)
ADREF:    ptr[uint8] = ptr(0x009A)

ADRES:    ptr[uint16] = ptr(0x008C) # 16-bit Result
ADRESL:   ptr[uint8] = ptr(0x008C)
ADRESH:   ptr[uint8] = ptr(0x008D)

# ==========================================
#  UART / EUSART (Bank 2)
# ==========================================
RC1REG:   ptr[uint8] = ptr(0x0119)
TX1REG:   ptr[uint8] = ptr(0x011A)
SP1BRG:   ptr[uint16] = ptr(0x011B) # 16-bit Baud Rate
SP1BRGL:  ptr[uint8] = ptr(0x011B)
SP1BRGH:  ptr[uint8] = ptr(0x011C)
RC1STA:   ptr[uint8] = ptr(0x011D)
TX1STA:   ptr[uint8] = ptr(0x011E)
BAUD1CON: ptr[uint8] = ptr(0x011F)

# Aliases for compatibility
RCREG:    ptr[uint8] = RC1REG
TXREG:    ptr[uint8] = TX1REG
RCSTA:    ptr[uint8] = RC1STA
TXSTA:    ptr[uint8] = TX1STA

# ==========================================
#  PPS - Peripheral Pin Select (Bank 61)
# ==========================================
PPSLOCK:  ptr[uint8] = ptr(0x1E8F)
INTPPS:   ptr[uint8] = ptr(0x1E90)

# Input PPS (Select which pin drives the peripheral)
RXPPS:    ptr[uint8] = ptr(0x1ECB)
CKPPS:    ptr[uint8] = ptr(0x1EC5) # SSP1CLK

# Output PPS (Select which peripheral drives the pin)
# Located in Bank 62
RA0PPS:   ptr[uint8] = ptr(0x1F10)
RC6PPS:   ptr[uint8] = ptr(0x1F26) # Typical TX Pin
RC7PPS:   ptr[uint8] = ptr(0x1F27) # Typical RX Pin

# ==========================================
#  NCO - Numerically Controlled Oscillator (Bank 11)
# ==========================================
NCO1CON:  ptr[uint8] = ptr(0x0592)
NCO1CLK:  ptr[uint8] = ptr(0x0593)
# NCO Accumulator (20-bit, accessed as 3 bytes or 16+8)
NCO1ACC:  ptr[uint16] = ptr(0x058C) # Low + High
NCO1ACCU: ptr[uint8] = ptr(0x058E)  # Upper
# NCO Increment (Step size)
NCO1INC:  ptr[uint16] = ptr(0x058F) # Low + High
NCO1INCU: ptr[uint8] = ptr(0x0591)  # Upper

# ==========================================
#  CLC - Configurable Logic Cells (Bank 60)
# ==========================================
CLC1CON:  ptr[uint8] = ptr(0x1E10)
CLC1POL:  ptr[uint8] = ptr(0x1E11)
CLC1SEL0: ptr[uint8] = ptr(0x1E12)
CLC1SEL1: ptr[uint8] = ptr(0x1E13)
CLC1SEL2: ptr[uint8] = ptr(0x1E14)
CLC1SEL3: ptr[uint8] = ptr(0x1E15)
CLC1GLS0: ptr[uint8] = ptr(0x1E16)
CLC1GLS1: ptr[uint8] = ptr(0x1E17)
CLC1GLS2: ptr[uint8] = ptr(0x1E18)
CLC1GLS3: ptr[uint8] = ptr(0x1E19)

# ==========================================
#  NVM / EEPROM (Bank 16)
# ==========================================
NVMADR:   ptr[uint16] = ptr(0x081A)
NVMDAT:   ptr[uint16] = ptr(0x081C)
NVMCON1:  ptr[uint8] = ptr(0x081E)
NVMCON2:  ptr[uint8] = ptr(0x081F)

# ==========================================
#  Timer0 (Bank 0)
# ==========================================
TMR0L:    ptr[uint8] = ptr(0x001C)
TMR0H:    ptr[uint8] = ptr(0x001D)
T0CON0:   ptr[uint8] = ptr(0x001E)
T0CON1:   ptr[uint8] = ptr(0x001F)

# ==========================================
#  Timer1 (Bank 4)
# ==========================================
TMR1L:    ptr[uint8] = ptr(0x020C)
TMR1H:    ptr[uint8] = ptr(0x020D)
T1CON:    ptr[uint8] = ptr(0x020E)
T1GCON:   ptr[uint8] = ptr(0x020F)

# ==========================================
#  Timer2 (Bank 5)
# ==========================================
TMR2:     ptr[uint8] = ptr(0x028C)
T2PR:     ptr[uint8] = ptr(0x028D)
T2CON:    ptr[uint8] = ptr(0x028E)

# ==========================================
#  CCP1 (Bank 6)
# ==========================================
CCPR1L:   ptr[uint8] = ptr(0x030C)
CCPR1H:   ptr[uint8] = ptr(0x030D)
CCP1CON:  ptr[uint8] = ptr(0x030E)
CCP1CAP:  ptr[uint8] = ptr(0x030F)

# ==========================================
#  CCP2 (Bank 6)
# ==========================================
CCPR2L:   ptr[uint8] = ptr(0x0310)
CCPR2H:   ptr[uint8] = ptr(0x0311)
CCP2CON:  ptr[uint8] = ptr(0x0312)
CCP2CAP:  ptr[uint8] = ptr(0x0313)

# ==========================================
#  Interrupts (PIR/PIE) - Bank 14
# ==========================================
PIR0:     ptr[uint8] = ptr(0x070C)
PIR1:     ptr[uint8] = ptr(0x070D)
PIR2:     ptr[uint8] = ptr(0x070E)
PIR3:     ptr[uint8] = ptr(0x070F)

PIE0:     ptr[uint8] = ptr(0x0716)
PIE1:     ptr[uint8] = ptr(0x0717)
PIE2:     ptr[uint8] = ptr(0x0718)
PIE3:     ptr[uint8] = ptr(0x0719)

# ==========================================
#  Weak Pull-Up Registers (Bank 4)
# ==========================================
WPUA:     ptr[uint8] = ptr(0x020C)
WPUB:     ptr[uint8] = ptr(0x020D)
WPUC:     ptr[uint8] = ptr(0x020E)
WPUD:     ptr[uint8] = ptr(0x020F)
WPUE:     ptr[uint8] = ptr(0x0210)

# ==========================================
#  Interrupt-on-Change (IOC) Registers (Bank 7)
# ==========================================
IOCAP:    ptr[uint8] = ptr(0x0391)
IOCAN:    ptr[uint8] = ptr(0x0392)
IOCBP:    ptr[uint8] = ptr(0x0395)
IOCBN:    ptr[uint8] = ptr(0x0396)
IOCCP:    ptr[uint8] = ptr(0x0399)
IOCCN:    ptr[uint8] = ptr(0x039A)
IOCDP:    ptr[uint8] = ptr(0x039D)
IOCDN:    ptr[uint8] = ptr(0x039E)
IOCEP:    ptr[uint8] = ptr(0x03A1)
IOCEN:    ptr[uint8] = ptr(0x03A2)

# ==========================================
#  CONSTANTS & BITS
# ==========================================

# STATUS Bits
C:      int = 0
DC:     int = 1
Z:      int = 2
NOT_PD: int = 3
NOT_TO: int = 4

# INTCON Bits
GIE:    int = 7
PEIE:   int = 6

# PPS Constants
PPS_TX: int = 0x10 # TX1 Output function
PPS_RX: int = 0x17 # RX1 Input function (RC7)

# NVMCON1 Bits
RD:    int = 0
WR:    int = 1
WREN:  int = 2
WRERR: int = 3
FREE:  int = 4
LWLO:  int = 5
NVMREGS: int = 6

# T0CON0 Bits
T0EN:   int = 7
T0OUT:  int = 5
T016BIT: int = 4

# T0CON1 Bits
T0CS0:  int = 5; T0CS1: int = 6; T0CS2: int = 7
T0ASYNC: int = 4
T0CKPS0: int = 0; T0CKPS1: int = 1; T0CKPS2: int = 2; T0CKPS3: int = 3

# T1CON Bits
TMR1ON: int = 0
T1SYNC: int = 2
T1CKPS0: int = 4; T1CKPS1: int = 5

# T2CON Bits
T2ON:   int = 7
T2CKPS0: int = 0; T2CKPS1: int = 1; T2CKPS2: int = 2

# CCP1CON Bits
CCP1EN:  int = 7
CCP1MODE0: int = 0; CCP1MODE1: int = 1; CCP1MODE2: int = 2; CCP1MODE3: int = 3

# CCP2CON Bits
CCP2EN:  int = 7
CCP2MODE0: int = 0; CCP2MODE1: int = 1; CCP2MODE2: int = 2; CCP2MODE3: int = 3

# ADC Bits
ADON: int = 0
GO:   int = 0

# TXSTA Bits
TXEN: int = 5
BRGH: int = 2
TRMT: int = 1

# RCSTA Bits
SPEN: int = 7
CREN: int = 4

# PIR1 Bits
TMR1IF: int = 0
TMR2IF: int = 1
ADIF:   int = 6
RCIF:   int = 5
TXIF:   int = 4

# PIR4 Bits
TMR0IF: int = 0
CCP1IF: int = 1
CCP2IF: int = 2