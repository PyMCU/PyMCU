from whipsnake.chips.pic18f45k50 import TRISA, TRISB, TRISC, LATA, LATB, LATC, PORTA, PORTB, PORTC, INTCON, INTCON2, INTCON3
from whipsnake.types import uint8, inline

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
    elif name == "RA6":
        TRISA[6] = mode
    elif name == "RA7":
        TRISA[7] = mode
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
    elif name == "RA6":
        LATA[6] = 1
    elif name == "RA7":
        LATA[7] = 1
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
    elif name == "RA6":
        LATA[6] = 0
    elif name == "RA7":
        LATA[7] = 0
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
    elif name == "RA6":
        LATA[6] = LATA[6] ^ 1
    elif name == "RA7":
        LATA[7] = LATA[7] ^ 1
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
    elif name == "RA6":
        return PORTA[6]
    elif name == "RA7":
        return PORTA[7]
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
    elif name == "RA6":
        if val == 1:
            LATA[6] = 1
        elif val == 0:
            LATA[6] = 0
    elif name == "RA7":
        if val == 1:
            LATA[7] = 1
        elif val == 0:
            LATA[7] = 0
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

@inline
def pin_pull_up(name: str):
    if name == "RB0":
        INTCON2[7] = 0
    elif name == "RB1":
        INTCON2[7] = 0
    elif name == "RB2":
        INTCON2[7] = 0
    elif name == "RB3":
        INTCON2[7] = 0
    elif name == "RB4":
        INTCON2[7] = 0
    elif name == "RB5":
        INTCON2[7] = 0
    elif name == "RB6":
        INTCON2[7] = 0
    elif name == "RB7":
        INTCON2[7] = 0
    else:
        raise NotImplementedError("Pull-up not available on this pin for PIC18F45K50")

@inline
def pin_pull_off(name: str):
    if name == "RB0":
        INTCON2[7] = 1
    elif name == "RB1":
        INTCON2[7] = 1
    elif name == "RB2":
        INTCON2[7] = 1
    elif name == "RB3":
        INTCON2[7] = 1
    elif name == "RB4":
        INTCON2[7] = 1
    elif name == "RB5":
        INTCON2[7] = 1
    elif name == "RB6":
        INTCON2[7] = 1
    elif name == "RB7":
        INTCON2[7] = 1
    else:
        raise NotImplementedError("Pull-up not available on this pin for PIC18F45K50")

@inline
def pin_irq_setup(name: str, trigger: uint8):
    if name == "RB0":
        if trigger == 1:
            INTCON2[6] = 0
        elif trigger == 2:
            INTCON2[6] = 1
        INTCON[4] = 1
        INTCON[7] = 1
    elif name == "RB1":
        if trigger == 1:
            INTCON2[5] = 0
        elif trigger == 2:
            INTCON2[5] = 1
        INTCON3[3] = 1
        INTCON[7] = 1
    elif name == "RB2":
        if trigger == 1:
            INTCON2[4] = 0
        elif trigger == 2:
            INTCON2[4] = 1
        INTCON3[4] = 1
        INTCON[7] = 1
    elif name == "RB4":
        INTCON[3] = 1
        INTCON[7] = 1
    elif name == "RB5":
        INTCON[3] = 1
        INTCON[7] = 1
    elif name == "RB6":
        INTCON[3] = 1
        INTCON[7] = 1
    elif name == "RB7":
        INTCON[3] = 1
        INTCON[7] = 1
    else:
        raise NotImplementedError("IRQ not available on this pin for PIC18F45K50")
