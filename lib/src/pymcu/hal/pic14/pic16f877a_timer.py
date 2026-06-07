from pymcu.chips.pic16f877a import OPTION_REG, TMR0, INTCON
from pymcu.types import uint8, inline

@inline
def timer0_init(prescaler: uint8):
    if prescaler == 2:
        OPTION_REG.value = 0x80
    elif prescaler == 4:
        OPTION_REG.value = 0x81
    elif prescaler == 8:
        OPTION_REG.value = 0x82
    elif prescaler == 16:
        OPTION_REG.value = 0x83
    elif prescaler == 32:
        OPTION_REG.value = 0x84
    elif prescaler == 64:
        OPTION_REG.value = 0x85
    elif prescaler == 128:
        OPTION_REG.value = 0x86
    elif prescaler == 256:
        OPTION_REG.value = 0x87

@inline
def timer0_start():
    INTCON[5] = 1

@inline
def timer0_stop():
    INTCON[5] = 0

@inline
def timer0_clear():
    TMR0.value = 0
