from whipsnake.chips.pic16f18877 import T0CON0, T0CON1, TMR0L, TMR0H
from whipsnake.types import uint8, inline

@inline
def timer0_init(prescaler: uint8):
    T0CON0.value = 0x00
    if prescaler == 1:
        T0CON1.value = 0x40
    elif prescaler == 2:
        T0CON1.value = 0x41
    elif prescaler == 4:
        T0CON1.value = 0x42
    elif prescaler == 8:
        T0CON1.value = 0x43
    elif prescaler == 16:
        T0CON1.value = 0x44
    elif prescaler == 32:
        T0CON1.value = 0x45
    elif prescaler == 64:
        T0CON1.value = 0x46
    elif prescaler == 128:
        T0CON1.value = 0x47
    elif prescaler == 256:
        T0CON1.value = 0x48

@inline
def timer0_start():
    T0CON0[7] = 1

@inline
def timer0_stop():
    T0CON0[7] = 0

@inline
def timer0_clear():
    TMR0L.value = 0
    TMR0H.value = 0
