from whipsnake.chips.pic16f18877 import TX1STA, RC1STA, TX1REG, RC1REG, SP1BRGL, SP1BRGH, BAUD1CON, RC6PPS, RXPPS, TRISC
from whipsnake.types import uint8, inline

@inline
def uart_init(baud: uint8):
    RC6PPS.value = 0x10
    RXPPS.value = 0x17
    TRISC[6] = 0
    TRISC[7] = 1
    BAUD1CON.value = 0x08
    if baud == 96:
        SP1BRGL.value = 103
        SP1BRGH.value = 0
    elif baud == 192:
        SP1BRGL.value = 51
        SP1BRGH.value = 0
    elif baud == 384:
        SP1BRGL.value = 25
        SP1BRGH.value = 0
    elif baud == 576:
        SP1BRGL.value = 16
        SP1BRGH.value = 0
    elif baud == 1152:
        SP1BRGL.value = 8
        SP1BRGH.value = 0
    TX1STA.value = 0x24
    RC1STA.value = 0x90

@inline
def uart_write(data: uint8):
    while TX1STA[1] == 0:
        pass
    TX1REG.value = data

@inline
def uart_write_byte(data: uint8):
    TX1REG.value = data
