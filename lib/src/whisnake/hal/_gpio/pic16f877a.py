from whisnake.chips.pic16f877a import TRISA, TRISB, TRISC, TRISD, TRISE, PORTA, PORTB, PORTC, PORTD, PORTE, OPTION_REG, INTCON
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
        PORTA[0] = 1
    elif name == "RA1":
        PORTA[1] = 1
    elif name == "RA2":
        PORTA[2] = 1
    elif name == "RA3":
        PORTA[3] = 1
    elif name == "RA4":
        PORTA[4] = 1
    elif name == "RA5":
        PORTA[5] = 1
    elif name == "RB0":
        PORTB[0] = 1
    elif name == "RB1":
        PORTB[1] = 1
    elif name == "RB2":
        PORTB[2] = 1
    elif name == "RB3":
        PORTB[3] = 1
    elif name == "RB4":
        PORTB[4] = 1
    elif name == "RB5":
        PORTB[5] = 1
    elif name == "RB6":
        PORTB[6] = 1
    elif name == "RB7":
        PORTB[7] = 1
    elif name == "RC0":
        PORTC[0] = 1
    elif name == "RC1":
        PORTC[1] = 1
    elif name == "RC2":
        PORTC[2] = 1
    elif name == "RC3":
        PORTC[3] = 1
    elif name == "RC4":
        PORTC[4] = 1
    elif name == "RC5":
        PORTC[5] = 1
    elif name == "RC6":
        PORTC[6] = 1
    elif name == "RC7":
        PORTC[7] = 1
    elif name == "RD0":
        PORTD[0] = 1
    elif name == "RD1":
        PORTD[1] = 1
    elif name == "RD2":
        PORTD[2] = 1
    elif name == "RD3":
        PORTD[3] = 1
    elif name == "RD4":
        PORTD[4] = 1
    elif name == "RD5":
        PORTD[5] = 1
    elif name == "RD6":
        PORTD[6] = 1
    elif name == "RD7":
        PORTD[7] = 1
    elif name == "RE0":
        PORTE[0] = 1
    elif name == "RE1":
        PORTE[1] = 1
    elif name == "RE2":
        PORTE[2] = 1

@inline
def pin_low(name: str):
    if name == "RA0":
        PORTA[0] = 0
    elif name == "RA1":
        PORTA[1] = 0
    elif name == "RA2":
        PORTA[2] = 0
    elif name == "RA3":
        PORTA[3] = 0
    elif name == "RA4":
        PORTA[4] = 0
    elif name == "RA5":
        PORTA[5] = 0
    elif name == "RB0":
        PORTB[0] = 0
    elif name == "RB1":
        PORTB[1] = 0
    elif name == "RB2":
        PORTB[2] = 0
    elif name == "RB3":
        PORTB[3] = 0
    elif name == "RB4":
        PORTB[4] = 0
    elif name == "RB5":
        PORTB[5] = 0
    elif name == "RB6":
        PORTB[6] = 0
    elif name == "RB7":
        PORTB[7] = 0
    elif name == "RC0":
        PORTC[0] = 0
    elif name == "RC1":
        PORTC[1] = 0
    elif name == "RC2":
        PORTC[2] = 0
    elif name == "RC3":
        PORTC[3] = 0
    elif name == "RC4":
        PORTC[4] = 0
    elif name == "RC5":
        PORTC[5] = 0
    elif name == "RC6":
        PORTC[6] = 0
    elif name == "RC7":
        PORTC[7] = 0
    elif name == "RD0":
        PORTD[0] = 0
    elif name == "RD1":
        PORTD[1] = 0
    elif name == "RD2":
        PORTD[2] = 0
    elif name == "RD3":
        PORTD[3] = 0
    elif name == "RD4":
        PORTD[4] = 0
    elif name == "RD5":
        PORTD[5] = 0
    elif name == "RD6":
        PORTD[6] = 0
    elif name == "RD7":
        PORTD[7] = 0
    elif name == "RE0":
        PORTE[0] = 0
    elif name == "RE1":
        PORTE[1] = 0
    elif name == "RE2":
        PORTE[2] = 0

@inline
def pin_toggle(name: str):
    if name == "RA0":
        PORTA[0] = PORTA[0] ^ 1
    elif name == "RA1":
        PORTA[1] = PORTA[1] ^ 1
    elif name == "RA2":
        PORTA[2] = PORTA[2] ^ 1
    elif name == "RA3":
        PORTA[3] = PORTA[3] ^ 1
    elif name == "RA4":
        PORTA[4] = PORTA[4] ^ 1
    elif name == "RA5":
        PORTA[5] = PORTA[5] ^ 1
    elif name == "RB0":
        PORTB[0] = PORTB[0] ^ 1
    elif name == "RB1":
        PORTB[1] = PORTB[1] ^ 1
    elif name == "RB2":
        PORTB[2] = PORTB[2] ^ 1
    elif name == "RB3":
        PORTB[3] = PORTB[3] ^ 1
    elif name == "RB4":
        PORTB[4] = PORTB[4] ^ 1
    elif name == "RB5":
        PORTB[5] = PORTB[5] ^ 1
    elif name == "RB6":
        PORTB[6] = PORTB[6] ^ 1
    elif name == "RB7":
        PORTB[7] = PORTB[7] ^ 1
    elif name == "RC0":
        PORTC[0] = PORTC[0] ^ 1
    elif name == "RC1":
        PORTC[1] = PORTC[1] ^ 1
    elif name == "RC2":
        PORTC[2] = PORTC[2] ^ 1
    elif name == "RC3":
        PORTC[3] = PORTC[3] ^ 1
    elif name == "RC4":
        PORTC[4] = PORTC[4] ^ 1
    elif name == "RC5":
        PORTC[5] = PORTC[5] ^ 1
    elif name == "RC6":
        PORTC[6] = PORTC[6] ^ 1
    elif name == "RC7":
        PORTC[7] = PORTC[7] ^ 1
    elif name == "RD0":
        PORTD[0] = PORTD[0] ^ 1
    elif name == "RD1":
        PORTD[1] = PORTD[1] ^ 1
    elif name == "RD2":
        PORTD[2] = PORTD[2] ^ 1
    elif name == "RD3":
        PORTD[3] = PORTD[3] ^ 1
    elif name == "RD4":
        PORTD[4] = PORTD[4] ^ 1
    elif name == "RD5":
        PORTD[5] = PORTD[5] ^ 1
    elif name == "RD6":
        PORTD[6] = PORTD[6] ^ 1
    elif name == "RD7":
        PORTD[7] = PORTD[7] ^ 1
    elif name == "RE0":
        PORTE[0] = PORTE[0] ^ 1
    elif name == "RE1":
        PORTE[1] = PORTE[1] ^ 1
    elif name == "RE2":
        PORTE[2] = PORTE[2] ^ 1

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
            PORTA[0] = 1
        if val == 0:
            PORTA[0] = 0
    elif name == "RA1":
        if val == 1:
            PORTA[1] = 1
        if val == 0:
            PORTA[1] = 0
    elif name == "RA2":
        if val == 1:
            PORTA[2] = 1
        if val == 0:
            PORTA[2] = 0
    elif name == "RA3":
        if val == 1:
            PORTA[3] = 1
        if val == 0:
            PORTA[3] = 0
    elif name == "RA4":
        if val == 1:
            PORTA[4] = 1
        if val == 0:
            PORTA[4] = 0
    elif name == "RA5":
        if val == 1:
            PORTA[5] = 1
        if val == 0:
            PORTA[5] = 0
    elif name == "RB0":
        if val == 1:
            PORTB[0] = 1
        if val == 0:
            PORTB[0] = 0
    elif name == "RB1":
        if val == 1:
            PORTB[1] = 1
        if val == 0:
            PORTB[1] = 0
    elif name == "RB2":
        if val == 1:
            PORTB[2] = 1
        if val == 0:
            PORTB[2] = 0
    elif name == "RB3":
        if val == 1:
            PORTB[3] = 1
        if val == 0:
            PORTB[3] = 0
    elif name == "RB4":
        if val == 1:
            PORTB[4] = 1
        if val == 0:
            PORTB[4] = 0
    elif name == "RB5":
        if val == 1:
            PORTB[5] = 1
        if val == 0:
            PORTB[5] = 0
    elif name == "RB6":
        if val == 1:
            PORTB[6] = 1
        if val == 0:
            PORTB[6] = 0
    elif name == "RB7":
        if val == 1:
            PORTB[7] = 1
        if val == 0:
            PORTB[7] = 0
    elif name == "RC0":
        if val == 1:
            PORTC[0] = 1
        if val == 0:
            PORTC[0] = 0
    elif name == "RC1":
        if val == 1:
            PORTC[1] = 1
        if val == 0:
            PORTC[1] = 0
    elif name == "RC2":
        if val == 1:
            PORTC[2] = 1
        if val == 0:
            PORTC[2] = 0
    elif name == "RC3":
        if val == 1:
            PORTC[3] = 1
        if val == 0:
            PORTC[3] = 0
    elif name == "RC4":
        if val == 1:
            PORTC[4] = 1
        if val == 0:
            PORTC[4] = 0
    elif name == "RC5":
        if val == 1:
            PORTC[5] = 1
        if val == 0:
            PORTC[5] = 0
    elif name == "RC6":
        if val == 1:
            PORTC[6] = 1
        if val == 0:
            PORTC[6] = 0
    elif name == "RC7":
        if val == 1:
            PORTC[7] = 1
        if val == 0:
            PORTC[7] = 0
    elif name == "RD0":
        if val == 1:
            PORTD[0] = 1
        if val == 0:
            PORTD[0] = 0
    elif name == "RD1":
        if val == 1:
            PORTD[1] = 1
        if val == 0:
            PORTD[1] = 0
    elif name == "RD2":
        if val == 1:
            PORTD[2] = 1
        if val == 0:
            PORTD[2] = 0
    elif name == "RD3":
        if val == 1:
            PORTD[3] = 1
        if val == 0:
            PORTD[3] = 0
    elif name == "RD4":
        if val == 1:
            PORTD[4] = 1
        if val == 0:
            PORTD[4] = 0
    elif name == "RD5":
        if val == 1:
            PORTD[5] = 1
        if val == 0:
            PORTD[5] = 0
    elif name == "RD6":
        if val == 1:
            PORTD[6] = 1
        if val == 0:
            PORTD[6] = 0
    elif name == "RD7":
        if val == 1:
            PORTD[7] = 1
        if val == 0:
            PORTD[7] = 0
    elif name == "RE0":
        if val == 1:
            PORTE[0] = 1
        if val == 0:
            PORTE[0] = 0
    elif name == "RE1":
        if val == 1:
            PORTE[1] = 1
        if val == 0:
            PORTE[1] = 0
    elif name == "RE2":
        if val == 1:
            PORTE[2] = 1
        if val == 0:
            PORTE[2] = 0

@inline
def pin_pull_up(name: str):
    if name == "RB0":
        OPTION_REG[7] = 0
    elif name == "RB1":
        OPTION_REG[7] = 0
    elif name == "RB2":
        OPTION_REG[7] = 0
    elif name == "RB3":
        OPTION_REG[7] = 0
    elif name == "RB4":
        OPTION_REG[7] = 0
    elif name == "RB5":
        OPTION_REG[7] = 0
    elif name == "RB6":
        OPTION_REG[7] = 0
    elif name == "RB7":
        OPTION_REG[7] = 0
    else:
        raise NotImplementedError("Pull-up not available on this pin for PIC16F877A")

@inline
def pin_pull_off(name: str):
    if name == "RB0":
        OPTION_REG[7] = 1
    elif name == "RB1":
        OPTION_REG[7] = 1
    elif name == "RB2":
        OPTION_REG[7] = 1
    elif name == "RB3":
        OPTION_REG[7] = 1
    elif name == "RB4":
        OPTION_REG[7] = 1
    elif name == "RB5":
        OPTION_REG[7] = 1
    elif name == "RB6":
        OPTION_REG[7] = 1
    elif name == "RB7":
        OPTION_REG[7] = 1
    else:
        raise NotImplementedError("Pull-up not available on this pin for PIC16F877A")

@inline
def pin_irq_setup(name: str, trigger: uint8):
    if name == "RB0":
        if trigger == 1:
            OPTION_REG[6] = 0
        if trigger == 2:
            OPTION_REG[6] = 1
        INTCON[4] = 1
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
        raise NotImplementedError("IRQ not available on this pin for PIC16F877A")
