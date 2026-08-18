from pymcu.chips.pic10f200 import OPTION, TMR0
from pymcu.exceptions import CompileError
from pymcu.types import uint8, uint16, inline

OPTION_GPIO_BITS_AT_RESET = 0xC0


@inline
def timer0_init(prescaler: uint16):
    if prescaler == 2:
        OPTION.value = OPTION_GPIO_BITS_AT_RESET | 0x00
    elif prescaler == 4:
        OPTION.value = OPTION_GPIO_BITS_AT_RESET | 0x01
    elif prescaler == 8:
        OPTION.value = OPTION_GPIO_BITS_AT_RESET | 0x02
    elif prescaler == 16:
        OPTION.value = OPTION_GPIO_BITS_AT_RESET | 0x03
    elif prescaler == 32:
        OPTION.value = OPTION_GPIO_BITS_AT_RESET | 0x04
    elif prescaler == 64:
        OPTION.value = OPTION_GPIO_BITS_AT_RESET | 0x05
    elif prescaler == 128:
        OPTION.value = OPTION_GPIO_BITS_AT_RESET | 0x06
    elif prescaler == 256:
        OPTION.value = OPTION_GPIO_BITS_AT_RESET | 0x07
    else:
        raise CompileError("PIC10F200 Timer0 divides by a power of two from 2 to 256; any other prescaler would leave the timer at its reset value")


@inline
def timer0_clear():
    TMR0.value = 0


@inline
def timer0_read() -> uint8:
    return TMR0.value
