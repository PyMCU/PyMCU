from pymcu.chips.pic16f18877 import T0CON0, T0CON1, TMR0L, TMR0H
from pymcu.types import uint8, uint16, inline

@inline
def timer0_init(prescaler: uint16):
    # uint16, not uint8: the top prescaler this chip offers is 256, which a
    # uint8 cannot hold. Timer.__init__ declares uint16 and pic12 and the AVR
    # timers already agree; here the 256 arm was unreachable.
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
