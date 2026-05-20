from pymcu.chips.atmega328p import PORTB, PINB
from pymcu.types import uint8, asm


def drive_pin(bit: uint8, val: uint8):
    match bit:
        case 0:
            PORTB[0] = val
        case 1:
            PORTB[1] = val
        case 2:
            PORTB[2] = val
        case 3:
            PORTB[3] = val
        case _:
            pass


def main():
    b: uint8 = PINB.value
    drive_pin(b, 1)
    asm("BREAK")
