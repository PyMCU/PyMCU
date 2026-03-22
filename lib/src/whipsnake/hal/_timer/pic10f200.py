from whipsnake.chips.pic10f200 import OPTION, TMR0
from whipsnake.types import uint8, inline

@inline
def timer0_init(prescaler: uint8):
    if prescaler == 2:
        OPTION.value = 0x00
    elif prescaler == 4:
        OPTION.value = 0x01
    elif prescaler == 8:
        OPTION.value = 0x02
    elif prescaler == 16:
        OPTION.value = 0x03
    elif prescaler == 32:
        OPTION.value = 0x04
    elif prescaler == 64:
        OPTION.value = 0x05
    elif prescaler == 128:
        OPTION.value = 0x06
    elif prescaler == 256:
        OPTION.value = 0x07

@inline
def timer0_start():
    pass

@inline
def timer0_stop():
    pass

@inline
def timer0_clear():
    TMR0.value = 0
