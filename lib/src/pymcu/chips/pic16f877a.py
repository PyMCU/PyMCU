from pymcu.types import ptr, uint8

# ==========================================
#  Register Definitions (SRAM & SFRs)
# ==========================================

# ----- Bank 0 -----
INDF:     ptr[uint8] = ptr(0x00)
TMR0:     ptr[uint8] = ptr(0x01)
PCL:      ptr[uint8] = ptr(0x02)
STATUS:   ptr[uint8] = ptr(0x03)
FSR:      ptr[uint8] = ptr(0x04)
PORTA:    ptr[uint8] = ptr(0x05)
PORTB:    ptr[uint8] = ptr(0x06)
PORTC:    ptr[uint8] = ptr(0x07)
PORTD:    ptr[uint8] = ptr(0x08)
PORTE:    ptr[uint8] = ptr(0x09)
PCLATH:   ptr[uint8] = ptr(0x0A)
INTCON:   ptr[uint8] = ptr(0x0B)
PIR1:     ptr[uint8] = ptr(0x0C)
PIR2:     ptr[uint8] = ptr(0x0D)
TMR1L:    ptr[uint8] = ptr(0x0E) # TMR1 Low
TMR1H:    ptr[uint8] = ptr(0x0F) # TMR1 High
T1CON:    ptr[uint8] = ptr(0x10)
TMR2:     ptr[uint8] = ptr(0x11)
T2CON:    ptr[uint8] = ptr(0x12)
SSPBUF:   ptr[uint8] = ptr(0x13)
SSPCON:   ptr[uint8] = ptr(0x14)
CCPR1L:   ptr[uint8] = ptr(0x15)
CCPR1H:   ptr[uint8] = ptr(0x16)
CCP1CON:  ptr[uint8] = ptr(0x17)
RCSTA:    ptr[uint8] = ptr(0x18)
TXREG:    ptr[uint8] = ptr(0x19)
RCREG:    ptr[uint8] = ptr(0x1A)
CCPR2L:   ptr[uint8] = ptr(0x1B)
CCPR2H:   ptr[uint8] = ptr(0x1C)
CCP2CON:  ptr[uint8] = ptr(0x1D)
ADRESH:   ptr[uint8] = ptr(0x1E)
ADCON0:   ptr[uint8] = ptr(0x1F)

# ----- Bank 1 -----
OPTION_REG: ptr[uint8] = ptr(0x81)
TRISA:      ptr[uint8] = ptr(0x85)
TRISB:      ptr[uint8] = ptr(0x86)
TRISC:      ptr[uint8] = ptr(0x87)
TRISD:      ptr[uint8] = ptr(0x88)
TRISE:      ptr[uint8] = ptr(0x89)
PIE1:       ptr[uint8] = ptr(0x8C)
PIE2:       ptr[uint8] = ptr(0x8D)
PCON:       ptr[uint8] = ptr(0x8E)
SSPCON2:    ptr[uint8] = ptr(0x91)
PR2:        ptr[uint8] = ptr(0x92)
SSPADD:     ptr[uint8] = ptr(0x93)
SSPSTAT:    ptr[uint8] = ptr(0x94)
TXSTA:      ptr[uint8] = ptr(0x98)
SPBRG:      ptr[uint8] = ptr(0x99)
CMCON:      ptr[uint8] = ptr(0x9C)
CVRCON:     ptr[uint8] = ptr(0x9D)
ADRESL:     ptr[uint8] = ptr(0x9E)
ADCON1:     ptr[uint8] = ptr(0x9F)

# ----- Bank 2 -----
EEDATA:   ptr[uint8] = ptr(0x10C)
EEADR:    ptr[uint8] = ptr(0x10D)
EEDATH:   ptr[uint8] = ptr(0x10E)
EEADRH:   ptr[uint8] = ptr(0x10F)

# ----- Bank 3 -----
EECON1:   ptr[uint8] = ptr(0x18C)
EECON2:   ptr[uint8] = ptr(0x18D)


# ==========================================
#  Bit Definitions (0-7 Constants)
# ==========================================

# --- STATUS Bits ---
C:      int = 0
DC:     int = 1
Z:      int = 2
NOT_PD: int = 3
NOT_TO: int = 4
RP0:    int = 5
RP1:    int = 6
IRP:    int = 7

# --- PORTA Bits ---
RA0: int = 0; RA1: int = 1; RA2: int = 2; RA3: int = 3
RA4: int = 4; RA5: int = 5

# --- PORTB Bits ---
RB0: int = 0; RB1: int = 1; RB2: int = 2; RB3: int = 3
RB4: int = 4; RB5: int = 5; RB6: int = 6; RB7: int = 7

# --- PORTC Bits ---
RC0: int = 0; RC1: int = 1; RC2: int = 2; RC3: int = 3
RC4: int = 4; RC5: int = 5; RC6: int = 6; RC7: int = 7

# --- PORTD Bits ---
RD0: int = 0; RD1: int = 1; RD2: int = 2; RD3: int = 3
RD4: int = 4; RD5: int = 5; RD6: int = 6; RD7: int = 7

# --- PORTE Bits ---
RE0: int = 0; RE1: int = 1; RE2: int = 2

# --- INTCON Bits ---
RBIF:   int = 0
INTF:   int = 1
TMR0IF: int = 2
RBIE:   int = 3
INTE:   int = 4
TMR0IE: int = 5
PEIE:   int = 6
GIE:    int = 7

# --- PIR1 Bits ---
TMR1IF: int = 0
TMR2IF: int = 1
CCP1IF: int = 2
SSPIF:  int = 3
TXIF:   int = 4
RCIF:   int = 5
ADIF:   int = 6
PSPIF:  int = 7

# --- PIR2 Bits ---
CCP2IF: int = 0
BCLIF:  int = 3
EEIF:   int = 4
CMIF:   int = 6

# --- T1CON Bits ---
TMR1ON:     int = 0
TMR1CS:     int = 1
NOT_T1SYNC: int = 2
T1OSCEN:    int = 3
T1CKPS0:    int = 4
T1CKPS1:    int = 5

# --- T2CON Bits ---
T2CKPS0: int = 0
T2CKPS1: int = 1
TMR2ON:  int = 2
TOUTPS0: int = 3
TOUTPS1: int = 4
TOUTPS2: int = 5
TOUTPS3: int = 6

# --- SSPCON Bits ---
SSPM0: int = 0; SSPM1: int = 1; SSPM2: int = 2; SSPM3: int = 3
CKP:   int = 4; SSPEN: int = 5; SSPOV: int = 6; WCOL:  int = 7

# --- CCP1CON Bits ---
CCP1M0: int = 0; CCP1M1: int = 1; CCP1M2: int = 2; CCP1M3: int = 3
CCP1Y:  int = 4; CCP1X:  int = 5

# --- RCSTA Bits ---
RX9D:  int = 0
OERR:  int = 1
FERR:  int = 2
ADDEN: int = 3
CREN:  int = 4
SREN:  int = 5
RX9:   int = 6
SPEN:  int = 7

# --- TXSTA Bits ---
TX9D: int = 0
TRMT: int = 1
BRGH: int = 2
SYNC: int = 4
TXEN: int = 5
TX9:  int = 6
CSRC: int = 7

# --- ADCON0 Bits ---
ADON:        int = 0
GO_NOT_DONE: int = 2
GO:          int = 2 # Alias
CHS0:        int = 3
CHS1:        int = 4
CHS2:        int = 5
ADCS0:       int = 6
ADCS1:       int = 7

# --- OPTION_REG Bits ---
PS0:      int = 0
PS1:      int = 1
PS2:      int = 2
PSA:      int = 3
T0SE:     int = 4
T0CS:     int = 5
INTEDG:   int = 6
NOT_RBPU: int = 7

# --- TRIS Bits (Standard mapping) ---
# Usually matching PORT bits 0-7

# --- PCON Bits ---
NOT_BOR: int = 0
NOT_POR: int = 1

# --- SSPSTAT Bits ---
BF:      int = 0
UA:      int = 1
R_NOT_W: int = 2
S:       int = 3
P:       int = 4
D_NOT_A: int = 5
CKE:     int = 6
SMP:     int = 7

# --- EECON1 Bits ---
RD:    int = 0
WR:    int = 1
WREN:  int = 2
WRERR: int = 3
EEPGD: int = 7