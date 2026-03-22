from whipsnake.chips.pic16f877a import TXSTA, RCSTA, TXREG, RCREG, SPBRG, TRISC, PIR1
from whipsnake.types import uint8, inline

@inline
def uart_init(baud: uint8):
    TRISC[6] = 0
    TRISC[7] = 1
    if baud == 96:
        SPBRG.value = 25
    elif baud == 192:
        SPBRG.value = 12
    elif baud == 384:
        SPBRG.value = 6
    elif baud == 576:
        SPBRG.value = 3
    elif baud == 1152:
        SPBRG.value = 1
    TXSTA.value = 0x24
    RCSTA.value = 0x90

@inline
def uart_write(data: uint8):
    while TXSTA[1] == 0:
        pass
    TXREG.value = data

@inline
def uart_read_ready() -> uint8:
    pass

@inline
def uart_write_byte(data: uint8):
    TXREG.value = data
