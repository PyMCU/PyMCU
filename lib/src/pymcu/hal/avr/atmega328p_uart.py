from pymcu.chips.atmega328p import UCSR0A, UCSR0B, UCSR0C, UBRR0L, UBRR0H, UDR0, DDRD
from pymcu.types import uint8, inline

@inline
def uart_init(baud: uint8):
    DDRD[1] = 1
    DDRD[0] = 0
    if baud == 96:
        UBRR0L.value = 103
        UBRR0H.value = 0
    elif baud == 192:
        UBRR0L.value = 51
        UBRR0H.value = 0
    elif baud == 384:
        UBRR0L.value = 25
        UBRR0H.value = 0
    elif baud == 576:
        UBRR0L.value = 16
        UBRR0H.value = 0
    elif baud == 1152:
        UBRR0L.value = 8
        UBRR0H.value = 0
    UCSR0C.value = 0x06
    UCSR0B.value = 0x18

@inline
def uart_write(data: uint8):
    while UCSR0A[5] == 0:
        pass
    UDR0.value = data

@inline
def uart_write_byte(data: uint8):
    UDR0.value = data
