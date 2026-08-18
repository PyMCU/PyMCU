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
RAM_START = 0x20
RAM_SIZE = 224
FLASH_SIZE = 4096
FLASH_WORDS = 2048

device_info(chip="pic16f628a", arch="pic14", ram_size=RAM_SIZE, flash_size=FLASH_SIZE)

# ==========================================
#  Register Definitions (SFRs)
# ==========================================

# ----- Bank 0 -----
INDF:     ptr[uint8] = ptr(0x00)
TMR0:     ptr[uint8] = ptr(0x01)
PCL:      ptr[uint8] = ptr(0x02)
STATUS:   ptr[uint8] = ptr(0x03)
FSR:      ptr[uint8] = ptr(0x04)
PORTA:    ptr[uint8] = ptr(0x05)
PORTB:    ptr[uint8] = ptr(0x06)
PCLATH:   ptr[uint8] = ptr(0x0A)
INTCON:   ptr[uint8] = ptr(0x0B)
PIR1:     ptr[uint8] = ptr(0x0C)
TMR1L:    ptr[uint8] = ptr(0x0E)
TMR1H:    ptr[uint8] = ptr(0x0F)
TMR1:     ptr[uint16] = ptr(0x0E)
T1CON:    ptr[uint8] = ptr(0x10)
TMR2:     ptr[uint8] = ptr(0x11)
T2CON:    ptr[uint8] = ptr(0x12)
CCPR1L:   ptr[uint8] = ptr(0x15)
CCPR1H:   ptr[uint8] = ptr(0x16)
CCPR1:    ptr[uint16] = ptr(0x15)
CCP1CON:  ptr[uint8] = ptr(0x17)
RCSTA:    ptr[uint8] = ptr(0x18)
TXREG:    ptr[uint8] = ptr(0x19)
RCREG:    ptr[uint8] = ptr(0x1A)
CMCON:    ptr[uint8] = ptr(0x1F)

# ----- Bank 1 -----
OPTION_REG: ptr[uint8] = ptr(0x81)
TRISA:    ptr[uint8] = ptr(0x85)
TRISB:    ptr[uint8] = ptr(0x86)
PIE1:     ptr[uint8] = ptr(0x8C)
PCON:     ptr[uint8] = ptr(0x8E)
PR2:      ptr[uint8] = ptr(0x92)
TXSTA:    ptr[uint8] = ptr(0x98)
SPBRG:    ptr[uint8] = ptr(0x99)
EEDATA:   ptr[uint8] = ptr(0x9A)
EEADR:    ptr[uint8] = ptr(0x9B)
EECON1:   ptr[uint8] = ptr(0x9C)
EECON2:   ptr[uint8] = ptr(0x9D)
VRCON:    ptr[uint8] = ptr(0x9F)

# ==========================================
#  Bit Definitions
# ==========================================

# STATUS Bits
C:      int = 0
DC:     int = 1
Z:      int = 2
NOT_PD: int = 3
NOT_TO: int = 4
RP0:    int = 5
RP1:    int = 6
IRP:    int = 7

# PORTA Bits
RA0: int = 0
RA1: int = 1
RA2: int = 2
RA3: int = 3
RA4: int = 4
RA5: int = 5
RA6: int = 6
RA7: int = 7

# PORTB Bits
RB0: int = 0
RB1: int = 1
RB2: int = 2
RB3: int = 3
RB4: int = 4
RB5: int = 5
RB6: int = 6
RB7: int = 7

# INTCON Bits
RBIF:   int = 0
INTF:   int = 1
T0IF:   int = 2
TMR0IF: int = 2
RBIE:   int = 3
INTE:   int = 4
T0IE:   int = 5
TMR0IE: int = 5
PEIE:   int = 6
GIE:    int = 7

# PIR1 Bits
TMR1IF: int = 0
TMR2IF: int = 1
CCP1IF: int = 2
TXIF:   int = 4
RCIF:   int = 5
CMIF:   int = 6
EEIF:   int = 7

# PIE1 Bits
TMR1IE: int = 0
TMR2IE: int = 1
CCP1IE: int = 2
TXIE:   int = 4
RCIE:   int = 5
CMIE:   int = 6
EEIE:   int = 7

# T1CON Bits
TMR1ON:      int = 0
TMR1CS:      int = 1
NOT_T1SYNC:  int = 2
T1OSCEN:     int = 3
T1CKPS0:     int = 4
T1CKPS1:     int = 5

# T2CON Bits
T2CKPS0: int = 0
T2CKPS1: int = 1
TMR2ON:  int = 2
TOUTPS0: int = 3
TOUTPS1: int = 4
TOUTPS2: int = 5
TOUTPS3: int = 6

# CCP1CON Bits
CCP1M0: int = 0
CCP1M1: int = 1
CCP1M2: int = 2
CCP1M3: int = 3
CCP1Y:  int = 4
CCP1X:  int = 5

# RCSTA Bits
RX9D:  int = 0
OERR:  int = 1
FERR:  int = 2
ADEN:  int = 3
ADDEN: int = 3
CREN:  int = 4
SREN:  int = 5
RX9:   int = 6
SPEN:  int = 7

# TXSTA Bits
TX9D: int = 0
TRMT: int = 1
BRGH: int = 2
SYNC: int = 4
TXEN: int = 5
TX9:  int = 6
CSRC: int = 7

# OPTION_REG Bits
PS0:      int = 0
PS1:      int = 1
PS2:      int = 2
PSA:      int = 3
T0SE:     int = 4
T0CS:     int = 5
INTEDG:   int = 6
NOT_RBPU: int = 7

# TRISA Bits
TRISA0: int = 0
TRISA1: int = 1
TRISA2: int = 2
TRISA3: int = 3
TRISA4: int = 4
TRISA5: int = 5
TRISA6: int = 6
TRISA7: int = 7

# TRISB Bits
TRISB0: int = 0
TRISB1: int = 1
TRISB2: int = 2
TRISB3: int = 3
TRISB4: int = 4
TRISB5: int = 5
TRISB6: int = 6
TRISB7: int = 7

# PCON Bits
NOT_BOR: int = 0
NOT_BO:  int = 0
NOT_BOD: int = 0
NOT_POR: int = 1
OSCF:    int = 3

# CMCON Bits
CM0:   int = 0
CM1:   int = 1
CM2:   int = 2
CIS:   int = 3
C1INV: int = 4
C2INV: int = 5
C1OUT: int = 6
C2OUT: int = 7

# EECON1 Bits
RD:    int = 0
WR:    int = 1
WREN:  int = 2
WRERR: int = 3

# VRCON Bits
VR0:  int = 0
VR1:  int = 1
VR2:  int = 2
VR3:  int = 3
VRR:  int = 5
VROE: int = 6
VREN: int = 7
