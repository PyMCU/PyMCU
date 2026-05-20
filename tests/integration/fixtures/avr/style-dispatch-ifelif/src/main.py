from pymcu.chips.atmega328p import PORTB, PINB
from pymcu.types import uint8, asm


def drive_pin(bit: uint8, val: uint8):
    if bit == 0:
        PORTB[0] = val
    elif bit == 1:
        PORTB[1] = val
    elif bit == 2:
        PORTB[2] = val
    elif bit == 3:
        PORTB[3] = val
    else:
        pass


def main():
    b: uint8 = PINB.value
    drive_pin(b, 1)
    asm("BREAK")
