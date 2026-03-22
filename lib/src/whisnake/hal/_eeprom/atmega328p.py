from whisnake.chips.atmega328p import EECR, EEDR, EEARL, EEARH
from whisnake.types import uint8, uint16, inline

# ATmega328P EEPROM register bit positions
# EECR: bit 0 = EERE, bit 1 = EEPE, bit 2 = EEMPE, bit 3 = EERIE
# EECR I/O address = DATA 0x3F - 0x20 = 0x1F  (in low I/O space: sbi/cbi/sbis/sbic OK)
#
# Write timing: set EEMPE (bit 2), then EEPE (bit 1) within 4 CPU cycles.
# We use asm() to guarantee the critical window is met.

@inline
def eeprom_write(addr: uint16, value: uint8):
    # Wait for any previous EEPROM write to complete (poll EEPE = bit 1)
    while EECR[1] == 1:
        pass
    # Load address into EEAR (10-bit: EEARH[1:0] + EEARL[7:0])
    EEARL.value = uint8(addr)
    EEARH.value = uint8(addr >> 8)
    # Load data byte
    EEDR.value = value
    # Timed write: EEMPE then EEPE within 4 cycles
    # EECR I/O address = 0x1F; OUT takes 1 cycle; SBI takes 2 cycles
    # Sequence: ldi r16,4 / out 0x1f,r16 / sbi 0x1f,1 -> EEPE set 2 cycles after EEMPE
    asm("ldi r16, 0x04")   # EEMPE mask (bit 2)
    asm("out 0x1f, r16")   # EECR = EEMPE
    asm("sbi 0x1f, 1")     # EECR |= EEPE  (within 2 cycles of EEMPE)

@inline
def eeprom_read(addr: uint16) -> uint8:
    # Wait for any pending write to complete
    while EECR[1] == 1:
        pass
    # Set address
    EEARL.value = uint8(addr)
    EEARH.value = uint8(addr >> 8)
    # Trigger read (EERE = bit 0)
    EECR[0] = 1
    return EEDR.value
