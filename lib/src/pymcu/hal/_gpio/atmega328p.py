from pymcu.chips.atmega328p import DDRB, DDRC, DDRD, PORTB, PORTC, PORTD, PINB, PINC, PIND, EICRA, EIMSK, PCICR, PCMSK0, PCMSK1, PCMSK2, SREG
from pymcu.types import uint8, inline

@inline
def pin_set_mode(name: str, mode: uint8):
    if name == "PB0":
        if mode == 0:
            DDRB[0] = 1
        elif mode == 1:
            DDRB[0] = 0
    elif name == "PB1":
        if mode == 0:
            DDRB[1] = 1
        elif mode == 1:
            DDRB[1] = 0
    elif name == "PB2":
        if mode == 0:
            DDRB[2] = 1
        elif mode == 1:
            DDRB[2] = 0
    elif name == "PB3":
        if mode == 0:
            DDRB[3] = 1
        elif mode == 1:
            DDRB[3] = 0
    elif name == "PB4":
        if mode == 0:
            DDRB[4] = 1
        elif mode == 1:
            DDRB[4] = 0
    elif name == "PB5":
        if mode == 0:
            DDRB[5] = 1
        elif mode == 1:
            DDRB[5] = 0
    elif name == "PC0":
        if mode == 0:
            DDRC[0] = 1
        elif mode == 1:
            DDRC[0] = 0
    elif name == "PC1":
        if mode == 0:
            DDRC[1] = 1
        elif mode == 1:
            DDRC[1] = 0
    elif name == "PC2":
        if mode == 0:
            DDRC[2] = 1
        elif mode == 1:
            DDRC[2] = 0
    elif name == "PC3":
        if mode == 0:
            DDRC[3] = 1
        elif mode == 1:
            DDRC[3] = 0
    elif name == "PC4":
        if mode == 0:
            DDRC[4] = 1
        elif mode == 1:
            DDRC[4] = 0
    elif name == "PC5":
        if mode == 0:
            DDRC[5] = 1
        elif mode == 1:
            DDRC[5] = 0
    elif name == "PD0":
        if mode == 0:
            DDRD[0] = 1
        elif mode == 1:
            DDRD[0] = 0
    elif name == "PD1":
        if mode == 0:
            DDRD[1] = 1
        elif mode == 1:
            DDRD[1] = 0
    elif name == "PD2":
        if mode == 0:
            DDRD[2] = 1
        elif mode == 1:
            DDRD[2] = 0
    elif name == "PD3":
        if mode == 0:
            DDRD[3] = 1
        elif mode == 1:
            DDRD[3] = 0
    elif name == "PD4":
        if mode == 0:
            DDRD[4] = 1
        elif mode == 1:
            DDRD[4] = 0
    elif name == "PD5":
        if mode == 0:
            DDRD[5] = 1
        elif mode == 1:
            DDRD[5] = 0
    elif name == "PD6":
        if mode == 0:
            DDRD[6] = 1
        elif mode == 1:
            DDRD[6] = 0
    elif name == "PD7":
        if mode == 0:
            DDRD[7] = 1
        elif mode == 1:
            DDRD[7] = 0

@inline
def pin_high(name: str):
    if name == "PB0":
        PORTB[0] = 1
    elif name == "PB1":
        PORTB[1] = 1
    elif name == "PB2":
        PORTB[2] = 1
    elif name == "PB3":
        PORTB[3] = 1
    elif name == "PB4":
        PORTB[4] = 1
    elif name == "PB5":
        PORTB[5] = 1
    elif name == "PC0":
        PORTC[0] = 1
    elif name == "PC1":
        PORTC[1] = 1
    elif name == "PC2":
        PORTC[2] = 1
    elif name == "PC3":
        PORTC[3] = 1
    elif name == "PC4":
        PORTC[4] = 1
    elif name == "PC5":
        PORTC[5] = 1
    elif name == "PD0":
        PORTD[0] = 1
    elif name == "PD1":
        PORTD[1] = 1
    elif name == "PD2":
        PORTD[2] = 1
    elif name == "PD3":
        PORTD[3] = 1
    elif name == "PD4":
        PORTD[4] = 1
    elif name == "PD5":
        PORTD[5] = 1
    elif name == "PD6":
        PORTD[6] = 1
    elif name == "PD7":
        PORTD[7] = 1

@inline
def pin_low(name: str):
    if name == "PB0":
        PORTB[0] = 0
    elif name == "PB1":
        PORTB[1] = 0
    elif name == "PB2":
        PORTB[2] = 0
    elif name == "PB3":
        PORTB[3] = 0
    elif name == "PB4":
        PORTB[4] = 0
    elif name == "PB5":
        PORTB[5] = 0
    elif name == "PC0":
        PORTC[0] = 0
    elif name == "PC1":
        PORTC[1] = 0
    elif name == "PC2":
        PORTC[2] = 0
    elif name == "PC3":
        PORTC[3] = 0
    elif name == "PC4":
        PORTC[4] = 0
    elif name == "PC5":
        PORTC[5] = 0
    elif name == "PD0":
        PORTD[0] = 0
    elif name == "PD1":
        PORTD[1] = 0
    elif name == "PD2":
        PORTD[2] = 0
    elif name == "PD3":
        PORTD[3] = 0
    elif name == "PD4":
        PORTD[4] = 0
    elif name == "PD5":
        PORTD[5] = 0
    elif name == "PD6":
        PORTD[6] = 0
    elif name == "PD7":
        PORTD[7] = 0

@inline
def pin_toggle(name: str):
    if name == "PB0":
        PORTB[0] = PORTB[0] ^ 1
    elif name == "PB1":
        PORTB[1] = PORTB[1] ^ 1
    elif name == "PB2":
        PORTB[2] = PORTB[2] ^ 1
    elif name == "PB3":
        PORTB[3] = PORTB[3] ^ 1
    elif name == "PB4":
        PORTB[4] = PORTB[4] ^ 1
    elif name == "PB5":
        PORTB[5] = PORTB[5] ^ 1
    elif name == "PC0":
        PORTC[0] = PORTC[0] ^ 1
    elif name == "PC1":
        PORTC[1] = PORTC[1] ^ 1
    elif name == "PC2":
        PORTC[2] = PORTC[2] ^ 1
    elif name == "PC3":
        PORTC[3] = PORTC[3] ^ 1
    elif name == "PC4":
        PORTC[4] = PORTC[4] ^ 1
    elif name == "PC5":
        PORTC[5] = PORTC[5] ^ 1
    elif name == "PD0":
        PORTD[0] = PORTD[0] ^ 1
    elif name == "PD1":
        PORTD[1] = PORTD[1] ^ 1
    elif name == "PD2":
        PORTD[2] = PORTD[2] ^ 1
    elif name == "PD3":
        PORTD[3] = PORTD[3] ^ 1
    elif name == "PD4":
        PORTD[4] = PORTD[4] ^ 1
    elif name == "PD5":
        PORTD[5] = PORTD[5] ^ 1
    elif name == "PD6":
        PORTD[6] = PORTD[6] ^ 1
    elif name == "PD7":
        PORTD[7] = PORTD[7] ^ 1

@inline
def pin_read(name: str) -> uint8:
    if name == "PB0":
        return PINB[0]
    elif name == "PB1":
        return PINB[1]
    elif name == "PB2":
        return PINB[2]
    elif name == "PB3":
        return PINB[3]
    elif name == "PB4":
        return PINB[4]
    elif name == "PB5":
        return PINB[5]
    elif name == "PC0":
        return PINC[0]
    elif name == "PC1":
        return PINC[1]
    elif name == "PC2":
        return PINC[2]
    elif name == "PC3":
        return PINC[3]
    elif name == "PC4":
        return PINC[4]
    elif name == "PC5":
        return PINC[5]
    elif name == "PD0":
        return PIND[0]
    elif name == "PD1":
        return PIND[1]
    elif name == "PD2":
        return PIND[2]
    elif name == "PD3":
        return PIND[3]
    elif name == "PD4":
        return PIND[4]
    elif name == "PD5":
        return PIND[5]
    elif name == "PD6":
        return PIND[6]
    elif name == "PD7":
        return PIND[7]

@inline
def pin_write(name: str, val: uint8):
    if val == 1:
        if name == "PB0":
            PORTB[0] = 1
        elif name == "PB1":
            PORTB[1] = 1
        elif name == "PB2":
            PORTB[2] = 1
        elif name == "PB3":
            PORTB[3] = 1
        elif name == "PB4":
            PORTB[4] = 1
        elif name == "PB5":
            PORTB[5] = 1
        elif name == "PC0":
            PORTC[0] = 1
        elif name == "PC1":
            PORTC[1] = 1
        elif name == "PC2":
            PORTC[2] = 1
        elif name == "PC3":
            PORTC[3] = 1
        elif name == "PC4":
            PORTC[4] = 1
        elif name == "PC5":
            PORTC[5] = 1
        elif name == "PD0":
            PORTD[0] = 1
        elif name == "PD1":
            PORTD[1] = 1
        elif name == "PD2":
            PORTD[2] = 1
        elif name == "PD3":
            PORTD[3] = 1
        elif name == "PD4":
            PORTD[4] = 1
        elif name == "PD5":
            PORTD[5] = 1
        elif name == "PD6":
            PORTD[6] = 1
        elif name == "PD7":
            PORTD[7] = 1
    elif val == 0:
        if name == "PB0":
            PORTB[0] = 0
        elif name == "PB1":
            PORTB[1] = 0
        elif name == "PB2":
            PORTB[2] = 0
        elif name == "PB3":
            PORTB[3] = 0
        elif name == "PB4":
            PORTB[4] = 0
        elif name == "PB5":
            PORTB[5] = 0
        elif name == "PC0":
            PORTC[0] = 0
        elif name == "PC1":
            PORTC[1] = 0
        elif name == "PC2":
            PORTC[2] = 0
        elif name == "PC3":
            PORTC[3] = 0
        elif name == "PC4":
            PORTC[4] = 0
        elif name == "PC5":
            PORTC[5] = 0
        elif name == "PD0":
            PORTD[0] = 0
        elif name == "PD1":
            PORTD[1] = 0
        elif name == "PD2":
            PORTD[2] = 0
        elif name == "PD3":
            PORTD[3] = 0
        elif name == "PD4":
            PORTD[4] = 0
        elif name == "PD5":
            PORTD[5] = 0
        elif name == "PD6":
            PORTD[6] = 0
        elif name == "PD7":
            PORTD[7] = 0

@inline
def pin_pull_up(name: str):
    if name == "PB0":
        PORTB[0] = 1
    elif name == "PB1":
        PORTB[1] = 1
    elif name == "PB2":
        PORTB[2] = 1
    elif name == "PB3":
        PORTB[3] = 1
    elif name == "PB4":
        PORTB[4] = 1
    elif name == "PB5":
        PORTB[5] = 1
    elif name == "PC0":
        PORTC[0] = 1
    elif name == "PC1":
        PORTC[1] = 1
    elif name == "PC2":
        PORTC[2] = 1
    elif name == "PC3":
        PORTC[3] = 1
    elif name == "PC4":
        PORTC[4] = 1
    elif name == "PC5":
        PORTC[5] = 1
    elif name == "PD0":
        PORTD[0] = 1
    elif name == "PD1":
        PORTD[1] = 1
    elif name == "PD2":
        PORTD[2] = 1
    elif name == "PD3":
        PORTD[3] = 1
    elif name == "PD4":
        PORTD[4] = 1
    elif name == "PD5":
        PORTD[5] = 1
    elif name == "PD6":
        PORTD[6] = 1
    elif name == "PD7":
        PORTD[7] = 1

@inline
def pin_pull_off(name: str):
    if name == "PB0":
        PORTB[0] = 0
    elif name == "PB1":
        PORTB[1] = 0
    elif name == "PB2":
        PORTB[2] = 0
    elif name == "PB3":
        PORTB[3] = 0
    elif name == "PB4":
        PORTB[4] = 0
    elif name == "PB5":
        PORTB[5] = 0
    elif name == "PC0":
        PORTC[0] = 0
    elif name == "PC1":
        PORTC[1] = 0
    elif name == "PC2":
        PORTC[2] = 0
    elif name == "PC3":
        PORTC[3] = 0
    elif name == "PC4":
        PORTC[4] = 0
    elif name == "PC5":
        PORTC[5] = 0
    elif name == "PD0":
        PORTD[0] = 0
    elif name == "PD1":
        PORTD[1] = 0
    elif name == "PD2":
        PORTD[2] = 0
    elif name == "PD3":
        PORTD[3] = 0
    elif name == "PD4":
        PORTD[4] = 0
    elif name == "PD5":
        PORTD[5] = 0
    elif name == "PD6":
        PORTD[6] = 0
    elif name == "PD7":
        PORTD[7] = 0

@inline
def pin_irq_setup(name: str, trigger: uint8):
    if name == "PD2":
        if trigger == 1:
            EICRA[0] = 0
            EICRA[1] = 1
        elif trigger == 2:
            EICRA[0] = 1
            EICRA[1] = 1
        elif trigger == 4:
            EICRA[0] = 0
            EICRA[1] = 0
        EIMSK[0] = 1
        SREG[7] = 1
    elif name == "PD3":
        if trigger == 1:
            EICRA[2] = 0
            EICRA[3] = 1
        elif trigger == 2:
            EICRA[2] = 1
            EICRA[3] = 1
        elif trigger == 4:
            EICRA[2] = 0
            EICRA[3] = 0
        EIMSK[1] = 1
        SREG[7] = 1
    elif name == "PB0":
        PCICR[0] = 1
        PCMSK0[0] = 1
        SREG[7] = 1
    elif name == "PB1":
        PCICR[0] = 1
        PCMSK0[1] = 1
        SREG[7] = 1
    elif name == "PB2":
        PCICR[0] = 1
        PCMSK0[2] = 1
        SREG[7] = 1
    elif name == "PB3":
        PCICR[0] = 1
        PCMSK0[3] = 1
        SREG[7] = 1
    elif name == "PB4":
        PCICR[0] = 1
        PCMSK0[4] = 1
        SREG[7] = 1
    elif name == "PB5":
        PCICR[0] = 1
        PCMSK0[5] = 1
        SREG[7] = 1
    elif name == "PC0":
        PCICR[1] = 1
        PCMSK1[0] = 1
        SREG[7] = 1
    elif name == "PC1":
        PCICR[1] = 1
        PCMSK1[1] = 1
        SREG[7] = 1
    elif name == "PC2":
        PCICR[1] = 1
        PCMSK1[2] = 1
        SREG[7] = 1
    elif name == "PC3":
        PCICR[1] = 1
        PCMSK1[3] = 1
        SREG[7] = 1
    elif name == "PC4":
        PCICR[1] = 1
        PCMSK1[4] = 1
        SREG[7] = 1
    elif name == "PC5":
        PCICR[1] = 1
        PCMSK1[5] = 1
        SREG[7] = 1
    elif name == "PD0":
        PCICR[2] = 1
        PCMSK2[0] = 1
        SREG[7] = 1
    elif name == "PD1":
        PCICR[2] = 1
        PCMSK2[1] = 1
        SREG[7] = 1
    elif name == "PD4":
        PCICR[2] = 1
        PCMSK2[4] = 1
        SREG[7] = 1
    elif name == "PD5":
        PCICR[2] = 1
        PCMSK2[5] = 1
        SREG[7] = 1
    elif name == "PD6":
        PCICR[2] = 1
        PCMSK2[6] = 1
        SREG[7] = 1
    elif name == "PD7":
        PCICR[2] = 1
        PCMSK2[7] = 1
        SREG[7] = 1
