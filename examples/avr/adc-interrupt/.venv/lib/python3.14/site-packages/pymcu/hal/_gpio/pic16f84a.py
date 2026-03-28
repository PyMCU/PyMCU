from pymcu.chips.pic16f84a import TRISA, TRISB, PORTA, PORTB, OPTION_REG, INTCON
from pymcu.types import uint8, inline

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
        raise NotImplementedError("Pull-up not available on this pin for PIC16F84A")

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
        raise NotImplementedError("Pull-up not available on this pin for PIC16F84A")

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
        raise NotImplementedError("IRQ not available on this pin for PIC16F84A")
