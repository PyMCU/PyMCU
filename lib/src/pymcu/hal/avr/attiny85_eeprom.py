from pymcu.chips.attiny85 import EECR, EEDR, EEAR, EEARH
from pymcu.types import uint8, uint16, inline, asm

# ATtiny85 EEPROM HAL
#
# 512 bytes EEPROM (9-bit address: 0x000 - 0x1FF).
# Registers:
#   EECR  at I/O 0x1C (data 0x3C) -- in low I/O space (SBI/CBI/SBIS/SBIC OK)
#   EEDR  at I/O 0x1D (data 0x3D)
#   EEAR  at I/O 0x1E (data 0x3E) -- EEARL (low 8 bits)
#   EEARH at I/O 0x1F (data 0x3F) -- EEARH (bit 0 only = address bit 8)

@inline
def eeprom_write(addr: uint16, value: uint8):
    while EECR[1]:
        pass
    EEAR.value = uint8(addr)
    EEARH.value = uint8(addr >> 8) & 0x01
    EEDR.value = value
    asm("ldi r16, 0x04")   # EEMPE mask (bit 2)
    asm("out 0x1c, r16")   # EECR = EEMPE (I/O 0x1C)
    asm("sbi 0x1c, 1")     # EECR |= EEPE within 2 cycles of EEMPE

@inline
def eeprom_read(addr: uint16) -> uint8:
    while EECR[1]:
        pass
    EEAR.value = uint8(addr)
    EEARH.value = uint8(addr >> 8) & 0x01
    EECR[0] = 1   # EERE: trigger read
    return EEDR.value
