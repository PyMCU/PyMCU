# PyMCU -- neg32-correctness: 32-bit unary negation must use the full
# NEG/COM+SBCI carry chain across all four bytes.
#
# Bug: the old codegen only emitted NEG R24 and left R25/R22/R23 untouched,
# producing completely wrong results for any non-zero int32 value.
#
# The correct sequence (avr-gcc compatible):
#   NEG  R24               ; R24 = -byte0,  C = (byte0 != 0)
#   COM  R25 ; SBCI R25,255 ; R25 = ~byte1 + 1 - C
#   COM  R22 ; SBCI R22,255 ; R22 = ~byte2 + 1 - C
#   COM  R23 ; SBCI R23,255 ; R23 = ~byte3 + 1 - C
#
# Checkpoints (ATmega328P data-space):
#   GPIOR0 (0x3E) = byte0 of neg(0x00000005) = 0xFB
#   GPIOR1 (0x4A) = byte1 of neg(0x00000005) = 0xFF
#   GPIOR2 (0x4B) = byte2 of neg(0x00000005) = 0xFF
#   OCR0A  (0x47) = byte3 of neg(0x00000005) = 0xFF
#   OCR0B  (0x48) = byte0 of neg(0x00010000) = 0x00  (critical: lo bytes are 0)
#   OCR1AL (0x88) = byte1 of neg(0x00010000) = 0x00
#   OCR1AH (0x89) = byte2 of neg(0x00010000) = 0xFF
#   OCR1BL (0x8A) = byte3 of neg(0x00010000) = 0xFF
#
from pymcu.types import uint8, int32, asm
from pymcu.chips.atmega328p import GPIOR0, GPIOR1, GPIOR2, OCR0A, OCR0B
from pymcu.chips.atmega328p import OCR1AL, OCR1AH, OCR1BL


def main():
    # Case 1: neg(5) = -5 = 0xFFFFFFFB
    a: int32 = 5
    b: int32 = -a
    GPIOR0.value = uint8(b & 0xFF)           # 0xFB
    GPIOR1.value = uint8((b >> 8) & 0xFF)    # 0xFF
    GPIOR2.value = uint8((b >> 16) & 0xFF)   # 0xFF
    OCR0A.value  = uint8((b >> 24) & 0xFF)   # 0xFF

    # Case 2: neg(0x00010000) = 0xFFFF0000
    # All low bytes of the input are 0 -- this is the case that catches the bug:
    # without carry propagation byte2 and byte3 end up wrong.
    c: int32 = 65536
    d: int32 = -c
    OCR0B.value  = uint8(d & 0xFF)           # byte0: 0x00
    OCR1AL.value = uint8((d >> 8) & 0xFF)    # byte1: 0x00
    OCR1AH.value = uint8((d >> 16) & 0xFF)   # byte2: 0xFF
    OCR1BL.value = uint8((d >> 24) & 0xFF)   # byte3: 0xFF

    asm("BREAK")
    while True:
        pass
