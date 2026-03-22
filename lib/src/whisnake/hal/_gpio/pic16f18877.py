from whisnake.chips.pic16f18877 import TRISA, TRISB, TRISC, TRISD, TRISE, LATA, LATB, LATC, LATD, LATE, PORTA, PORTB, PORTC, PORTD, PORTE, WPUA, WPUB, WPUC, WPUD, WPUE, IOCAP, IOCAN, IOCBP, IOCBN, IOCCP, IOCCN, IOCDP, IOCDN, IOCEP, IOCEN, INTCON, PIE0
from whisnake.types import uint8, inline

@inline
def pin_set_mode(name: str, mode: uint8):
    if name == "RA0":
        TRISA[0] = mode
    elif name == "RA1":
        TRISA[1] = mode
    elif name == "RA2":
        TRISA[2] = mode
    elif name == "RA3":
        TRISA[3] = mode
    elif name == "RA4":
        TRISA[4] = mode
    elif name == "RA5":
        TRISA[5] = mode
    elif name == "RB0":
        TRISB[0] = mode
    elif name == "RB1":
        TRISB[1] = mode
    elif name == "RB2":
        TRISB[2] = mode
    elif name == "RB3":
        TRISB[3] = mode
    elif name == "RB4":
        TRISB[4] = mode
    elif name == "RB5":
        TRISB[5] = mode
    elif name == "RB6":
        TRISB[6] = mode
    elif name == "RB7":
        TRISB[7] = mode
    elif name == "RC0":
        TRISC[0] = mode
    elif name == "RC1":
        TRISC[1] = mode
    elif name == "RC2":
        TRISC[2] = mode
    elif name == "RC3":
        TRISC[3] = mode
    elif name == "RC4":
        TRISC[4] = mode
    elif name == "RC5":
        TRISC[5] = mode
    elif name == "RC6":
        TRISC[6] = mode
    elif name == "RC7":
        TRISC[7] = mode
    elif name == "RD0":
        TRISD[0] = mode
    elif name == "RD1":
        TRISD[1] = mode
    elif name == "RD2":
        TRISD[2] = mode
    elif name == "RD3":
        TRISD[3] = mode
    elif name == "RD4":
        TRISD[4] = mode
    elif name == "RD5":
        TRISD[5] = mode
    elif name == "RD6":
        TRISD[6] = mode
    elif name == "RD7":
        TRISD[7] = mode
    elif name == "RE0":
        TRISE[0] = mode
    elif name == "RE1":
        TRISE[1] = mode
    elif name == "RE2":
        TRISE[2] = mode

@inline
def pin_high(name: str):
    if name == "RA0":
        LATA[0] = 1
    elif name == "RA1":
        LATA[1] = 1
    elif name == "RA2":
        LATA[2] = 1
    elif name == "RA3":
        LATA[3] = 1
    elif name == "RA4":
        LATA[4] = 1
    elif name == "RA5":
        LATA[5] = 1
    elif name == "RB0":
        LATB[0] = 1
    elif name == "RB1":
        LATB[1] = 1
    elif name == "RB2":
        LATB[2] = 1
    elif name == "RB3":
        LATB[3] = 1
    elif name == "RB4":
        LATB[4] = 1
    elif name == "RB5":
        LATB[5] = 1
    elif name == "RB6":
        LATB[6] = 1
    elif name == "RB7":
        LATB[7] = 1
    elif name == "RC0":
        LATC[0] = 1
    elif name == "RC1":
        LATC[1] = 1
    elif name == "RC2":
        LATC[2] = 1
    elif name == "RC3":
        LATC[3] = 1
    elif name == "RC4":
        LATC[4] = 1
    elif name == "RC5":
        LATC[5] = 1
    elif name == "RC6":
        LATC[6] = 1
    elif name == "RC7":
        LATC[7] = 1
    elif name == "RD0":
        LATD[0] = 1
    elif name == "RD1":
        LATD[1] = 1
    elif name == "RD2":
        LATD[2] = 1
    elif name == "RD3":
        LATD[3] = 1
    elif name == "RD4":
        LATD[4] = 1
    elif name == "RD5":
        LATD[5] = 1
    elif name == "RD6":
        LATD[6] = 1
    elif name == "RD7":
        LATD[7] = 1
    elif name == "RE0":
        LATE[0] = 1
    elif name == "RE1":
        LATE[1] = 1
    elif name == "RE2":
        LATE[2] = 1

@inline
def pin_low(name: str):
    if name == "RA0":
        LATA[0] = 0
    elif name == "RA1":
        LATA[1] = 0
    elif name == "RA2":
        LATA[2] = 0
    elif name == "RA3":
        LATA[3] = 0
    elif name == "RA4":
        LATA[4] = 0
    elif name == "RA5":
        LATA[5] = 0
    elif name == "RB0":
        LATB[0] = 0
    elif name == "RB1":
        LATB[1] = 0
    elif name == "RB2":
        LATB[2] = 0
    elif name == "RB3":
        LATB[3] = 0
    elif name == "RB4":
        LATB[4] = 0
    elif name == "RB5":
        LATB[5] = 0
    elif name == "RB6":
        LATB[6] = 0
    elif name == "RB7":
        LATB[7] = 0
    elif name == "RC0":
        LATC[0] = 0
    elif name == "RC1":
        LATC[1] = 0
    elif name == "RC2":
        LATC[2] = 0
    elif name == "RC3":
        LATC[3] = 0
    elif name == "RC4":
        LATC[4] = 0
    elif name == "RC5":
        LATC[5] = 0
    elif name == "RC6":
        LATC[6] = 0
    elif name == "RC7":
        LATC[7] = 0
    elif name == "RD0":
        LATD[0] = 0
    elif name == "RD1":
        LATD[1] = 0
    elif name == "RD2":
        LATD[2] = 0
    elif name == "RD3":
        LATD[3] = 0
    elif name == "RD4":
        LATD[4] = 0
    elif name == "RD5":
        LATD[5] = 0
    elif name == "RD6":
        LATD[6] = 0
    elif name == "RD7":
        LATD[7] = 0
    elif name == "RE0":
        LATE[0] = 0
    elif name == "RE1":
        LATE[1] = 0
    elif name == "RE2":
        LATE[2] = 0

@inline
def pin_toggle(name: str):
    if name == "RA0":
        LATA[0] = LATA[0] ^ 1
    elif name == "RA1":
        LATA[1] = LATA[1] ^ 1
    elif name == "RA2":
        LATA[2] = LATA[2] ^ 1
    elif name == "RA3":
        LATA[3] = LATA[3] ^ 1
    elif name == "RA4":
        LATA[4] = LATA[4] ^ 1
    elif name == "RA5":
        LATA[5] = LATA[5] ^ 1
    elif name == "RB0":
        LATB[0] = LATB[0] ^ 1
    elif name == "RB1":
        LATB[1] = LATB[1] ^ 1
    elif name == "RB2":
        LATB[2] = LATB[2] ^ 1
    elif name == "RB3":
        LATB[3] = LATB[3] ^ 1
    elif name == "RB4":
        LATB[4] = LATB[4] ^ 1
    elif name == "RB5":
        LATB[5] = LATB[5] ^ 1
    elif name == "RB6":
        LATB[6] = LATB[6] ^ 1
    elif name == "RB7":
        LATB[7] = LATB[7] ^ 1
    elif name == "RC0":
        LATC[0] = LATC[0] ^ 1
    elif name == "RC1":
        LATC[1] = LATC[1] ^ 1
    elif name == "RC2":
        LATC[2] = LATC[2] ^ 1
    elif name == "RC3":
        LATC[3] = LATC[3] ^ 1
    elif name == "RC4":
        LATC[4] = LATC[4] ^ 1
    elif name == "RC5":
        LATC[5] = LATC[5] ^ 1
    elif name == "RC6":
        LATC[6] = LATC[6] ^ 1
    elif name == "RC7":
        LATC[7] = LATC[7] ^ 1
    elif name == "RD0":
        LATD[0] = LATD[0] ^ 1
    elif name == "RD1":
        LATD[1] = LATD[1] ^ 1
    elif name == "RD2":
        LATD[2] = LATD[2] ^ 1
    elif name == "RD3":
        LATD[3] = LATD[3] ^ 1
    elif name == "RD4":
        LATD[4] = LATD[4] ^ 1
    elif name == "RD5":
        LATD[5] = LATD[5] ^ 1
    elif name == "RD6":
        LATD[6] = LATD[6] ^ 1
    elif name == "RD7":
        LATD[7] = LATD[7] ^ 1
    elif name == "RE0":
        LATE[0] = LATE[0] ^ 1
    elif name == "RE1":
        LATE[1] = LATE[1] ^ 1
    elif name == "RE2":
        LATE[2] = LATE[2] ^ 1

@inline
def pin_read(name: str) -> uint8:
    if name == "RA0":
        return PORTA[0]
    elif name == "RA1":
        return PORTA[1]
    elif name == "RA2":
        return PORTA[2]
    elif name == "RA3":
        return PORTA[3]
    elif name == "RA4":
        return PORTA[4]
    elif name == "RA5":
        return PORTA[5]
    elif name == "RB0":
        return PORTB[0]
    elif name == "RB1":
        return PORTB[1]
    elif name == "RB2":
        return PORTB[2]
    elif name == "RB3":
        return PORTB[3]
    elif name == "RB4":
        return PORTB[4]
    elif name == "RB5":
        return PORTB[5]
    elif name == "RB6":
        return PORTB[6]
    elif name == "RB7":
        return PORTB[7]
    elif name == "RC0":
        return PORTC[0]
    elif name == "RC1":
        return PORTC[1]
    elif name == "RC2":
        return PORTC[2]
    elif name == "RC3":
        return PORTC[3]
    elif name == "RC4":
        return PORTC[4]
    elif name == "RC5":
        return PORTC[5]
    elif name == "RC6":
        return PORTC[6]
    elif name == "RC7":
        return PORTC[7]
    elif name == "RD0":
        return PORTD[0]
    elif name == "RD1":
        return PORTD[1]
    elif name == "RD2":
        return PORTD[2]
    elif name == "RD3":
        return PORTD[3]
    elif name == "RD4":
        return PORTD[4]
    elif name == "RD5":
        return PORTD[5]
    elif name == "RD6":
        return PORTD[6]
    elif name == "RD7":
        return PORTD[7]
    elif name == "RE0":
        return PORTE[0]
    elif name == "RE1":
        return PORTE[1]
    elif name == "RE2":
        return PORTE[2]

@inline
def pin_write(name: str, val: uint8):
    if name == "RA0":
        if val == 1:
            LATA[0] = 1
        elif val == 0:
            LATA[0] = 0
    elif name == "RA1":
        if val == 1:
            LATA[1] = 1
        elif val == 0:
            LATA[1] = 0
    elif name == "RA2":
        if val == 1:
            LATA[2] = 1
        elif val == 0:
            LATA[2] = 0
    elif name == "RA3":
        if val == 1:
            LATA[3] = 1
        elif val == 0:
            LATA[3] = 0
    elif name == "RA4":
        if val == 1:
            LATA[4] = 1
        elif val == 0:
            LATA[4] = 0
    elif name == "RA5":
        if val == 1:
            LATA[5] = 1
        elif val == 0:
            LATA[5] = 0
    elif name == "RB0":
        if val == 1:
            LATB[0] = 1
        elif val == 0:
            LATB[0] = 0
    elif name == "RB1":
        if val == 1:
            LATB[1] = 1
        elif val == 0:
            LATB[1] = 0
    elif name == "RB2":
        if val == 1:
            LATB[2] = 1
        elif val == 0:
            LATB[2] = 0
    elif name == "RB3":
        if val == 1:
            LATB[3] = 1
        elif val == 0:
            LATB[3] = 0
    elif name == "RB4":
        if val == 1:
            LATB[4] = 1
        elif val == 0:
            LATB[4] = 0
    elif name == "RB5":
        if val == 1:
            LATB[5] = 1
        elif val == 0:
            LATB[5] = 0
    elif name == "RB6":
        if val == 1:
            LATB[6] = 1
        elif val == 0:
            LATB[6] = 0
    elif name == "RB7":
        if val == 1:
            LATB[7] = 1
        elif val == 0:
            LATB[7] = 0
    elif name == "RC0":
        if val == 1:
            LATC[0] = 1
        elif val == 0:
            LATC[0] = 0
    elif name == "RC1":
        if val == 1:
            LATC[1] = 1
        elif val == 0:
            LATC[1] = 0
    elif name == "RC2":
        if val == 1:
            LATC[2] = 1
        elif val == 0:
            LATC[2] = 0
    elif name == "RC3":
        if val == 1:
            LATC[3] = 1
        elif val == 0:
            LATC[3] = 0
    elif name == "RC4":
        if val == 1:
            LATC[4] = 1
        elif val == 0:
            LATC[4] = 0
    elif name == "RC5":
        if val == 1:
            LATC[5] = 1
        elif val == 0:
            LATC[5] = 0
    elif name == "RC6":
        if val == 1:
            LATC[6] = 1
        elif val == 0:
            LATC[6] = 0
    elif name == "RC7":
        if val == 1:
            LATC[7] = 1
        elif val == 0:
            LATC[7] = 0
    elif name == "RD0":
        if val == 1:
            LATD[0] = 1
        elif val == 0:
            LATD[0] = 0
    elif name == "RD1":
        if val == 1:
            LATD[1] = 1
        elif val == 0:
            LATD[1] = 0
    elif name == "RD2":
        if val == 1:
            LATD[2] = 1
        elif val == 0:
            LATD[2] = 0
    elif name == "RD3":
        if val == 1:
            LATD[3] = 1
        elif val == 0:
            LATD[3] = 0
    elif name == "RD4":
        if val == 1:
            LATD[4] = 1
        elif val == 0:
            LATD[4] = 0
    elif name == "RD5":
        if val == 1:
            LATD[5] = 1
        elif val == 0:
            LATD[5] = 0
    elif name == "RD6":
        if val == 1:
            LATD[6] = 1
        elif val == 0:
            LATD[6] = 0
    elif name == "RD7":
        if val == 1:
            LATD[7] = 1
        elif val == 0:
            LATD[7] = 0
    elif name == "RE0":
        if val == 1:
            LATE[0] = 1
        elif val == 0:
            LATE[0] = 0
    elif name == "RE1":
        if val == 1:
            LATE[1] = 1
        elif val == 0:
            LATE[1] = 0
    elif name == "RE2":
        if val == 1:
            LATE[2] = 1
        elif val == 0:
            LATE[2] = 0

@inline
def pin_pull_up(name: str):
    if name == "RA0":
        WPUA[0] = 1
    elif name == "RA1":
        WPUA[1] = 1
    elif name == "RA2":
        WPUA[2] = 1
    elif name == "RA3":
        WPUA[3] = 1
    elif name == "RA4":
        WPUA[4] = 1
    elif name == "RA5":
        WPUA[5] = 1
    elif name == "RB0":
        WPUB[0] = 1
    elif name == "RB1":
        WPUB[1] = 1
    elif name == "RB2":
        WPUB[2] = 1
    elif name == "RB3":
        WPUB[3] = 1
    elif name == "RB4":
        WPUB[4] = 1
    elif name == "RB5":
        WPUB[5] = 1
    elif name == "RB6":
        WPUB[6] = 1
    elif name == "RB7":
        WPUB[7] = 1
    elif name == "RC0":
        WPUC[0] = 1
    elif name == "RC1":
        WPUC[1] = 1
    elif name == "RC2":
        WPUC[2] = 1
    elif name == "RC3":
        WPUC[3] = 1
    elif name == "RC4":
        WPUC[4] = 1
    elif name == "RC5":
        WPUC[5] = 1
    elif name == "RC6":
        WPUC[6] = 1
    elif name == "RC7":
        WPUC[7] = 1
    elif name == "RD0":
        WPUD[0] = 1
    elif name == "RD1":
        WPUD[1] = 1
    elif name == "RD2":
        WPUD[2] = 1
    elif name == "RD3":
        WPUD[3] = 1
    elif name == "RD4":
        WPUD[4] = 1
    elif name == "RD5":
        WPUD[5] = 1
    elif name == "RD6":
        WPUD[6] = 1
    elif name == "RD7":
        WPUD[7] = 1
    elif name == "RE0":
        WPUE[0] = 1
    elif name == "RE1":
        WPUE[1] = 1
    elif name == "RE2":
        WPUE[2] = 1

@inline
def pin_pull_off(name: str):
    if name == "RA0":
        WPUA[0] = 0
    elif name == "RA1":
        WPUA[1] = 0
    elif name == "RA2":
        WPUA[2] = 0
    elif name == "RA3":
        WPUA[3] = 0
    elif name == "RA4":
        WPUA[4] = 0
    elif name == "RA5":
        WPUA[5] = 0
    elif name == "RB0":
        WPUB[0] = 0
    elif name == "RB1":
        WPUB[1] = 0
    elif name == "RB2":
        WPUB[2] = 0
    elif name == "RB3":
        WPUB[3] = 0
    elif name == "RB4":
        WPUB[4] = 0
    elif name == "RB5":
        WPUB[5] = 0
    elif name == "RB6":
        WPUB[6] = 0
    elif name == "RB7":
        WPUB[7] = 0
    elif name == "RC0":
        WPUC[0] = 0
    elif name == "RC1":
        WPUC[1] = 0
    elif name == "RC2":
        WPUC[2] = 0
    elif name == "RC3":
        WPUC[3] = 0
    elif name == "RC4":
        WPUC[4] = 0
    elif name == "RC5":
        WPUC[5] = 0
    elif name == "RC6":
        WPUC[6] = 0
    elif name == "RC7":
        WPUC[7] = 0
    elif name == "RD0":
        WPUD[0] = 0
    elif name == "RD1":
        WPUD[1] = 0
    elif name == "RD2":
        WPUD[2] = 0
    elif name == "RD3":
        WPUD[3] = 0
    elif name == "RD4":
        WPUD[4] = 0
    elif name == "RD5":
        WPUD[5] = 0
    elif name == "RD6":
        WPUD[6] = 0
    elif name == "RD7":
        WPUD[7] = 0
    elif name == "RE0":
        WPUE[0] = 0
    elif name == "RE1":
        WPUE[1] = 0
    elif name == "RE2":
        WPUE[2] = 0

@inline
def pin_irq_setup(name: str, trigger: uint8):
    if name == "RA0":
        if trigger == 1:
            IOCAN[0] = 1
        elif trigger == 2:
            IOCAP[0] = 1
        elif trigger == 3:
            IOCAP[0] = 1
            IOCAN[0] = 1
        PIE0[4] = 1
        INTCON[7] = 1
    elif name == "RA1":
        if trigger == 1:
            IOCAN[1] = 1
        elif trigger == 2:
            IOCAP[1] = 1
        elif trigger == 3:
            IOCAP[1] = 1
            IOCAN[1] = 1
        PIE0[4] = 1
        INTCON[7] = 1
    elif name == "RA2":
        if trigger == 1:
            IOCAN[2] = 1
        elif trigger == 2:
            IOCAP[2] = 1
        elif trigger == 3:
            IOCAP[2] = 1
            IOCAN[2] = 1
        PIE0[4] = 1
        INTCON[7] = 1
    elif name == "RA3":
        if trigger == 1:
            IOCAN[3] = 1
        elif trigger == 2:
            IOCAP[3] = 1
        elif trigger == 3:
            IOCAP[3] = 1
            IOCAN[3] = 1
        PIE0[4] = 1
        INTCON[7] = 1
    elif name == "RA4":
        if trigger == 1:
            IOCAN[4] = 1
        elif trigger == 2:
            IOCAP[4] = 1
        elif trigger == 3:
            IOCAP[4] = 1
            IOCAN[4] = 1
        PIE0[4] = 1
        INTCON[7] = 1
    elif name == "RA5":
        if trigger == 1:
            IOCAN[5] = 1
        elif trigger == 2:
            IOCAP[5] = 1
        elif trigger == 3:
            IOCAP[5] = 1
            IOCAN[5] = 1
        PIE0[4] = 1
        INTCON[7] = 1
    elif name == "RB0":
        if trigger == 1:
            IOCBN[0] = 1
        elif trigger == 2:
            IOCBP[0] = 1
        elif trigger == 3:
            IOCBP[0] = 1
            IOCBN[0] = 1
        PIE0[4] = 1
        INTCON[7] = 1
    elif name == "RB1":
        if trigger == 1:
            IOCBN[1] = 1
        elif trigger == 2:
            IOCBP[1] = 1
        elif trigger == 3:
            IOCBP[1] = 1
            IOCBN[1] = 1
        PIE0[4] = 1
        INTCON[7] = 1
    elif name == "RB2":
        if trigger == 1:
            IOCBN[2] = 1
        elif trigger == 2:
            IOCBP[2] = 1
        elif trigger == 3:
            IOCBP[2] = 1
            IOCBN[2] = 1
        PIE0[4] = 1
        INTCON[7] = 1
    elif name == "RB3":
        if trigger == 1:
            IOCBN[3] = 1
        elif trigger == 2:
            IOCBP[3] = 1
        elif trigger == 3:
            IOCBP[3] = 1
            IOCBN[3] = 1
        PIE0[4] = 1
        INTCON[7] = 1
    elif name == "RB4":
        if trigger == 1:
            IOCBN[4] = 1
        elif trigger == 2:
            IOCBP[4] = 1
        elif trigger == 3:
            IOCBP[4] = 1
            IOCBN[4] = 1
        PIE0[4] = 1
        INTCON[7] = 1
    elif name == "RB5":
        if trigger == 1:
            IOCBN[5] = 1
        elif trigger == 2:
            IOCBP[5] = 1
        elif trigger == 3:
            IOCBP[5] = 1
            IOCBN[5] = 1
        PIE0[4] = 1
        INTCON[7] = 1
    elif name == "RB6":
        if trigger == 1:
            IOCBN[6] = 1
        elif trigger == 2:
            IOCBP[6] = 1
        elif trigger == 3:
            IOCBP[6] = 1
            IOCBN[6] = 1
        PIE0[4] = 1
        INTCON[7] = 1
    elif name == "RB7":
        if trigger == 1:
            IOCBN[7] = 1
        elif trigger == 2:
            IOCBP[7] = 1
        elif trigger == 3:
            IOCBP[7] = 1
            IOCBN[7] = 1
        PIE0[4] = 1
        INTCON[7] = 1
    elif name == "RC0":
        if trigger == 1:
            IOCCN[0] = 1
        elif trigger == 2:
            IOCCP[0] = 1
        elif trigger == 3:
            IOCCP[0] = 1
            IOCCN[0] = 1
        PIE0[4] = 1
        INTCON[7] = 1
    elif name == "RC1":
        if trigger == 1:
            IOCCN[1] = 1
        elif trigger == 2:
            IOCCP[1] = 1
        elif trigger == 3:
            IOCCP[1] = 1
            IOCCN[1] = 1
        PIE0[4] = 1
        INTCON[7] = 1
    elif name == "RC2":
        if trigger == 1:
            IOCCN[2] = 1
        elif trigger == 2:
            IOCCP[2] = 1
        elif trigger == 3:
            IOCCP[2] = 1
            IOCCN[2] = 1
        PIE0[4] = 1
        INTCON[7] = 1
    elif name == "RC3":
        if trigger == 1:
            IOCCN[3] = 1
        elif trigger == 2:
            IOCCP[3] = 1
        elif trigger == 3:
            IOCCP[3] = 1
            IOCCN[3] = 1
        PIE0[4] = 1
        INTCON[7] = 1
    elif name == "RC4":
        if trigger == 1:
            IOCCN[4] = 1
        elif trigger == 2:
            IOCCP[4] = 1
        elif trigger == 3:
            IOCCP[4] = 1
            IOCCN[4] = 1
        PIE0[4] = 1
        INTCON[7] = 1
    elif name == "RC5":
        if trigger == 1:
            IOCCN[5] = 1
        elif trigger == 2:
            IOCCP[5] = 1
        elif trigger == 3:
            IOCCP[5] = 1
            IOCCN[5] = 1
        PIE0[4] = 1
        INTCON[7] = 1
    elif name == "RC6":
        if trigger == 1:
            IOCCN[6] = 1
        elif trigger == 2:
            IOCCP[6] = 1
        elif trigger == 3:
            IOCCP[6] = 1
            IOCCN[6] = 1
        PIE0[4] = 1
        INTCON[7] = 1
    elif name == "RC7":
        if trigger == 1:
            IOCCN[7] = 1
        elif trigger == 2:
            IOCCP[7] = 1
        elif trigger == 3:
            IOCCP[7] = 1
            IOCCN[7] = 1
        PIE0[4] = 1
        INTCON[7] = 1
    elif name == "RD0":
        if trigger == 1:
            IOCDN[0] = 1
        elif trigger == 2:
            IOCDP[0] = 1
        elif trigger == 3:
            IOCDP[0] = 1
            IOCDN[0] = 1
        PIE0[4] = 1
        INTCON[7] = 1
    elif name == "RD1":
        if trigger == 1:
            IOCDN[1] = 1
        elif trigger == 2:
            IOCDP[1] = 1
        elif trigger == 3:
            IOCDP[1] = 1
            IOCDN[1] = 1
        PIE0[4] = 1
        INTCON[7] = 1
    elif name == "RD2":
        if trigger == 1:
            IOCDN[2] = 1
        elif trigger == 2:
            IOCDP[2] = 1
        elif trigger == 3:
            IOCDP[2] = 1
            IOCDN[2] = 1
        PIE0[4] = 1
        INTCON[7] = 1
    elif name == "RD3":
        if trigger == 1:
            IOCDN[3] = 1
        elif trigger == 2:
            IOCDP[3] = 1
        elif trigger == 3:
            IOCDP[3] = 1
            IOCDN[3] = 1
        PIE0[4] = 1
        INTCON[7] = 1
    elif name == "RD4":
        if trigger == 1:
            IOCDN[4] = 1
        elif trigger == 2:
            IOCDP[4] = 1
        elif trigger == 3:
            IOCDP[4] = 1
            IOCDN[4] = 1
        PIE0[4] = 1
        INTCON[7] = 1
    elif name == "RD5":
        if trigger == 1:
            IOCDN[5] = 1
        elif trigger == 2:
            IOCDP[5] = 1
        elif trigger == 3:
            IOCDP[5] = 1
            IOCDN[5] = 1
        PIE0[4] = 1
        INTCON[7] = 1
    elif name == "RD6":
        if trigger == 1:
            IOCDN[6] = 1
        elif trigger == 2:
            IOCDP[6] = 1
        elif trigger == 3:
            IOCDP[6] = 1
            IOCDN[6] = 1
        PIE0[4] = 1
        INTCON[7] = 1
    elif name == "RD7":
        if trigger == 1:
            IOCDN[7] = 1
        elif trigger == 2:
            IOCDP[7] = 1
        elif trigger == 3:
            IOCDP[7] = 1
            IOCDN[7] = 1
        PIE0[4] = 1
        INTCON[7] = 1
    elif name == "RE0":
        if trigger == 1:
            IOCEN[0] = 1
        elif trigger == 2:
            IOCEP[0] = 1
        elif trigger == 3:
            IOCEP[0] = 1
            IOCEN[0] = 1
        PIE0[4] = 1
        INTCON[7] = 1
    elif name == "RE1":
        if trigger == 1:
            IOCEN[1] = 1
        elif trigger == 2:
            IOCEP[1] = 1
        elif trigger == 3:
            IOCEP[1] = 1
            IOCEN[1] = 1
        PIE0[4] = 1
        INTCON[7] = 1
    elif name == "RE2":
        if trigger == 1:
            IOCEN[2] = 1
        elif trigger == 2:
            IOCEP[2] = 1
        elif trigger == 3:
            IOCEP[2] = 1
            IOCEN[2] = 1
        PIE0[4] = 1
        INTCON[7] = 1
