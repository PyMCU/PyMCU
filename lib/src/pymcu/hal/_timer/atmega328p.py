from pymcu.chips.atmega328p import TCCR0A, TCCR0B, TCNT0, TIMSK0, TIFR0
from pymcu.types import uint8, inline

@inline
def timer0_init(prescaler: uint8):
    TCCR0A.value = 0x00
    if prescaler == 1:
        TCCR0B.value = 0x01
    elif prescaler == 8:
        TCCR0B.value = 0x02
    elif prescaler == 64:
        TCCR0B.value = 0x03
    elif prescaler == 256:
        TCCR0B.value = 0x04

@inline
def timer0_start():
    TIMSK0[0] = 1

@inline
def timer0_stop():
    TIMSK0[0] = 0
    TCCR0B.value = 0x00

@inline
def timer0_clear():
    TCNT0.value = 0
