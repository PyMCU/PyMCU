from whisnake.chips.pic18f45k50 import TXSTA, RCSTA, TXREG, RCREG, SPBRG, SPBRGH, BAUDCON, TRISC, PIR1
from whisnake.types import uint8, inline

@inline
def uart_init(baud: uint8):
    TRISC[6] = 0
    TRISC[7] = 1
    BAUDCON.value = 0x08
    if baud == 96:
        SPBRG.value = 103
        SPBRGH.value = 0
    elif baud == 192:
        SPBRG.value = 51
        SPBRGH.value = 0
    elif baud == 384:
        SPBRG.value = 25
        SPBRGH.value = 0
    elif baud == 576:
        SPBRG.value = 16
        SPBRGH.value = 0
    elif baud == 1152:
        SPBRG.value = 8
        SPBRGH.value = 0
    TXSTA.value = 0x24
    RCSTA.value = 0x90

@inline
def uart_write(data: uint8):
    while TXSTA[1] == 0:
        pass
    TXREG.value = data

@inline
def uart_write_byte(data: uint8):
    TXREG.value = data
