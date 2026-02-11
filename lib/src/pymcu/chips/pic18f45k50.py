from pymcu.types import ptr, uint8

# Device Memory Configuration for PIC18F45K50
# RAM: 2048 bytes total (Access Bank + GPR)
# Access Bank: 0x000-0x05F
# GPR Bank 0: 0x060-0x0FF (160 bytes)
# GPR Banks 1-14: 0x100-0xEFF (additional banks)
RAM_START = 0x060
RAM_SIZE = 2048

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

# Bits
RA0 = 0; RA1 = 1; RA2 = 2; RA3 = 3; RA4 = 4; RA5 = 5; RA6 = 6; RA7 = 7
RB0 = 0; RB1 = 1; RB2 = 2; RB3 = 3; RB4 = 4; RB5 = 5; RB6 = 6; RB7 = 7
RC0 = 0; RC1 = 1; RC2 = 2; RC6 = 6; RC7 = 7
