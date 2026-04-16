# PyMCU -- ptr-rw-roundtrip: ptr[uint8] round-trip and ptr[uint16] read-back.
#
# memory-layout (existing fixture) tests ptr[uint16] WRITE only:
#   ICR1.value = 16000  ->  STS 0x86 / STS 0x87
#
# This fixture tests the READ-BACK path:
#   w: uint16 = ICR1.value  ->  LDS pair (two 1-byte LDS instructions for H and L)
#
# Checkpoint 1: ptr[uint8] write/read round-trip via GPIOR0
#   Write 0xAB to GPIOR0 (OUT), read back (IN), store to GPIOR1; must equal 0xAB.
#
# Checkpoint 2: ptr[uint16] write, read back into uint16 variable, copy to OCR1A
#   ICR1 = 12345 (0x3039) -> read ICR1.value -> w -> OCR1A = w
#   Both ICR1 and OCR1A must contain 12345 (verified with HaveWordAt and HaveByteAt).
#
# Data-space addresses (ATmega328P):
#   GPIOR0 = 0x3E   GPIOR1 = 0x4A
#   ICR1   = 0x86 (ptr[uint16]: ICR1L=0x86, ICR1H=0x87, little-endian)
#   OCR1A  = 0x88 (ptr[uint16]: OCR1AL=0x88, OCR1AH=0x89, little-endian)
from pymcu.chips.atmega328p import GPIOR0, GPIOR1, ICR1, OCR1A
from pymcu.types import uint8, uint16, asm


def main():
    # --- Checkpoint 1: ptr[uint8] write/read round-trip ---
    GPIOR0.value = 0xAB          # OUT: write magic byte to GPIOR0
    readback: uint8 = GPIOR0.value   # IN:  read it back; must equal 0xAB
    GPIOR1.value = readback      # store to GPIOR1 for test inspection
    asm("BREAK")

    # --- Checkpoint 2: ptr[uint16] write, READ-BACK, derive, write ---
    ICR1.value = 12345           # 0x3039: STS pair write to 0x86/0x87
    w: uint16 = ICR1.value       # LDS pair READ-BACK (untested path in memory-layout)
    OCR1A.value = w              # write the read-back value to OCR1A (0x88/0x89)
    asm("BREAK")

    while True:
        pass
