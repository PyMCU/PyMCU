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
#
# EECR bit layout (same as ATmega328P EECR):
#   bit 3: EERIE (interrupt enable)
#   bit 2: EEMPE (master write enable -- timed window)
#   bit 1: EEPE  (write enable)
#   bit 0: EERE  (read enable)
#
# Timed write sequence:
#   1. Poll EEPE (bit 1) until clear.
#   2. Write address to EEAR (and EEARH for addr > 0xFF).
#   3. Write data to EEDR.
#   4. Set EEMPE (bit 2), then within 4 cycles set EEPE (bit 1).
#
# EECR is at I/O 0x1C; use OUT 0x1c instead of STS for the timed sequence.

@inline
def eeprom_write(addr: uint16, value: uint8):
    while EECR[1] == 1:
        pass
    EEAR.value = uint8(addr)
    EEARH.value = uint8(addr >> 8) & 0x01
    EEDR.value = value
    asm("ldi r16, 0x04")   # EEMPE mask (bit 2)
    asm("out 0x1c, r16")   # EECR = EEMPE (I/O 0x1C)
    asm("sbi 0x1c, 1")     # EECR |= EEPE within 2 cycles of EEMPE

@inline
def eeprom_read(addr: uint16) -> uint8:
    while EECR[1] == 1:
        pass
    EEAR.value = uint8(addr)
    EEARH.value = uint8(addr >> 8) & 0x01
    EECR[0] = 1   # EERE: trigger read
    return EEDR.value
