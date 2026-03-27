# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------

from pymcu.types import ptr, uint8, device_info

# Device Memory Configuration for PIC18F45K50
# RAM: 2048 bytes total (Access Bank + GPR)
# Access Bank: 0x000-0x05F
# GPR Bank 0: 0x060-0x0FF (160 bytes)
# GPR Banks 1-14: 0x100-0xEFF (additional banks)
RAM_START = 0x060
RAM_SIZE = 2048

device_info(chip="pic18f45k50", arch="pic18", ram_size=RAM_SIZE)

# SFRs for PIC18F45K50 (Partial)
PORTA: ptr[uint8] = ptr(0xF80)
PORTB: ptr[uint8] = ptr(0xF81)
PORTC: ptr[uint8] = ptr(0xF82)

TRISA: ptr[uint8] = ptr(0xF92)
TRISB: ptr[uint8] = ptr(0xF93)
TRISC: ptr[uint8] = ptr(0xF94)

LATA:  ptr[uint8] = ptr(0xF89)
LATB:  ptr[uint8] = ptr(0xF8A)
LATC:  ptr[uint8] = ptr(0xF8B)

ANSELA: ptr[uint8] = ptr(0xF38)
ANSELB: ptr[uint8] = ptr(0xF39)
ANSELC: ptr[uint8] = ptr(0xF3A)

PORTD: ptr[uint8] = ptr(0xF83)
PORTE: ptr[uint8] = ptr(0xF84)
TRISD: ptr[uint8] = ptr(0xF95)
TRISE: ptr[uint8] = ptr(0xF96)
LATD:  ptr[uint8] = ptr(0xF8C)
LATE:  ptr[uint8] = ptr(0xF8D)

INTCON: ptr[uint8] = ptr(0xFF2)
INTCON2: ptr[uint8] = ptr(0xFF1)
INTCON3: ptr[uint8] = ptr(0xFF0)

# ADC
ADCON0: ptr[uint8] = ptr(0xFC2)
ADCON1: ptr[uint8] = ptr(0xFC1)
ADCON2: ptr[uint8] = ptr(0xFC0)
ADRESH: ptr[uint8] = ptr(0xFC4)
ADRESL: ptr[uint8] = ptr(0xFC3)

# UART
TXSTA:  ptr[uint8] = ptr(0xFAC)
RCSTA:  ptr[uint8] = ptr(0xFAB)
TXREG:  ptr[uint8] = ptr(0xFAD)
RCREG:  ptr[uint8] = ptr(0xFAE)
SPBRG:  ptr[uint8] = ptr(0xFAF)
SPBRGH: ptr[uint8] = ptr(0xFB0)
BAUDCON: ptr[uint8] = ptr(0xFB8)

# Timer0
T0CON:  ptr[uint8] = ptr(0xFD5)
TMR0L:  ptr[uint8] = ptr(0xFD6)
TMR0H:  ptr[uint8] = ptr(0xFD7)

# Timer1
T1CON:  ptr[uint8] = ptr(0xFCD)
TMR1L:  ptr[uint8] = ptr(0xFCE)
TMR1H:  ptr[uint8] = ptr(0xFCF)

# Timer2
T2CON:  ptr[uint8] = ptr(0xFBA)
PR2:    ptr[uint8] = ptr(0xFBB)
TMR2:   ptr[uint8] = ptr(0xFBC)

# Timer3
T3CON:  ptr[uint8] = ptr(0xFB1)
TMR3L:  ptr[uint8] = ptr(0xFB2)
TMR3H:  ptr[uint8] = ptr(0xFB3)

# CCP1
CCP1CON: ptr[uint8] = ptr(0xFBD)
CCPR1L:  ptr[uint8] = ptr(0xFBE)
CCPR1H:  ptr[uint8] = ptr(0xFBF)

# CCP2
CCP2CON: ptr[uint8] = ptr(0xF97)
CCPR2L:  ptr[uint8] = ptr(0xF90)
CCPR2H:  ptr[uint8] = ptr(0xF91)

# Interrupts
PIR1:   ptr[uint8] = ptr(0xF9E)
PIR2:   ptr[uint8] = ptr(0xFA1)
PIE1:   ptr[uint8] = ptr(0xF9D)
PIE2:   ptr[uint8] = ptr(0xFA0)

# Bits
RA0 = 0; RA1 = 1; RA2 = 2; RA3 = 3; RA4 = 4; RA5 = 5; RA6 = 6; RA7 = 7
RB0 = 0; RB1 = 1; RB2 = 2; RB3 = 3; RB4 = 4; RB5 = 5; RB6 = 6; RB7 = 7
RC0 = 0; RC1 = 1; RC2 = 2; RC6 = 6; RC7 = 7

# ADCON0 Bits
ADON: int = 0; GO: int = 1
CHS0: int = 2; CHS1: int = 3; CHS2: int = 4; CHS3: int = 5

# TXSTA Bits
TX9D: int = 0; TRMT: int = 1; BRGH: int = 2; SENDB: int = 3
SYNC: int = 4; TXEN: int = 5; TX9: int = 6; CSRC: int = 7

# RCSTA Bits
RX9D: int = 0; OERR: int = 1; FERR: int = 2; ADDEN: int = 3
CREN: int = 4; SREN: int = 5; RX9: int = 6; SPEN: int = 7

# PIR1 Bits
TMR1IF: int = 0; TMR2IF: int = 1; CCP1IF: int = 2; SSPIF: int = 3
TXIF: int = 4; RCIF: int = 5; ADIF: int = 6

# T0CON Bits
T0PS0: int = 0; T0PS1: int = 1; T0PS2: int = 2
PSA: int = 3; T0SE: int = 4; T0CS: int = 5
T08BIT: int = 6; TMR0ON: int = 7

# T1CON Bits
TMR1ON: int = 0; T1SYNC: int = 2
T1CKPS0: int = 4; T1CKPS1: int = 5

# T2CON Bits
T2CKPS0: int = 0; T2CKPS1: int = 1
TMR2ON: int = 2
TOUTPS0: int = 3; TOUTPS1: int = 4; TOUTPS2: int = 5; TOUTPS3: int = 6

# CCP1CON Bits
CCP1M0: int = 0; CCP1M1: int = 1; CCP1M2: int = 2; CCP1M3: int = 3
DC1B0: int = 4; DC1B1: int = 5

# INTCON Bits
GIE: int = 7; PEIE: int = 6
