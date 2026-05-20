from pymcu.chips.atmega328p import EECR, EEDR, EEARL
from pymcu.types import uint8, asm


def poll_eeprom():
    while EECR[1]:
        pass
    asm("BREAK")


def poll_twi_zero():
    from pymcu.chips.atmega328p import TWCR
    while not TWCR[7]:
        pass
    asm("BREAK")


def main():
    poll_eeprom()
    poll_twi_zero()
