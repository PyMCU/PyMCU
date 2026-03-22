from whipsnake.chips.pic18f45k50 import T0CON, TMR0L, TMR0H
from whipsnake.types import uint8, inline

@inline
def timer0_init(prescaler: uint8):
    if prescaler == 2:
        T0CON.value = 0x40
    elif prescaler == 4:
        T0CON.value = 0x41
    elif prescaler == 8:
        T0CON.value = 0x42
    elif prescaler == 16:
        T0CON.value = 0x43
    elif prescaler == 32:
        T0CON.value = 0x44
    elif prescaler == 64:
        T0CON.value = 0x45
    elif prescaler == 128:
        T0CON.value = 0x46
    elif prescaler == 256:
        T0CON.value = 0x47

@inline
def timer0_start():
    T0CON[7] = 1

@inline
def timer0_stop():
    T0CON[7] = 0

@inline
def timer0_clear():
    TMR0L.value = 0
    TMR0H.value = 0
