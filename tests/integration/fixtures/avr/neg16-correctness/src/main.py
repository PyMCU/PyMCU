# PyMCU -- neg16-correctness: 16-bit unary negation must use NEG/COM/SBCI.
#
# Bug: The former codegen used ADC R25, R1 after NEG R24 + COM R25.
# ADC adds the carry bit, but the correct sequence subtracts it.
# For any int16 value whose low byte is 0 the carry from NEG R24 is 0,
# so ADC would leave the high byte one less than correct (0xFE instead of 0xFF).
#
# Example: -(int16(0x0100)) = -256 = 0xFF00
#   Wrong  (ADC): NEG R24 -> 0, C=0; COM R25 -> 0xFE; ADC R25,R1 -> 0xFE+0 (R1=0) = 0xFE  => 0xFE00
#   Correct (SBCI R25,255): NEG R24 -> 0, C=0; COM R25 -> 0xFE; 0xFE+1-0 = 0xFF    => 0xFF00
#
# Checkpoints (ATmega328P data-space):
#   GPIOR0 (0x3E) = low  byte of neg(0x0005) = 0xFB  (must not be 0 or wrong)
#   GPIOR1 (0x4A) = high byte of neg(0x0005) = 0xFF
#   GPIOR2 (0x4B) = low  byte of neg(0x0100) = 0x00
#   OCR0A  (0x47) = high byte of neg(0x0100) = 0xFF  (bug gives 0xFE here)
#   OCR0B  (0x48) = low  byte of neg(0x8000) = 0x00  (-32768 negates to -32768 in 16-bit)
#   OCR1AL (0x88) = high byte of neg(0x8000) = 0x80
#
from pymcu.types import uint8, int16, asm
from pymcu.chips.atmega328p import GPIOR0, GPIOR1, GPIOR2, OCR0A, OCR0B, OCR1AL


def main():
    # Case 1: neg(5) = -5 = 0xFFFB
    a: int16 = 5
    b: int16 = -a
    GPIOR0.value = uint8(b & 0xFF)         # low byte: 0xFB = 251
    GPIOR1.value = uint8((b >> 8) & 0xFF)  # high byte: 0xFF = 255

    # Case 2: neg(256) = -256 = 0xFF00 -- the critical bug case (lo byte == 0)
    c: int16 = 256
    d: int16 = -c
    GPIOR2.value = uint8(d & 0xFF)         # low byte: 0x00 = 0
    OCR0A.value  = uint8((d >> 8) & 0xFF)  # high byte: 0xFF = 255 (bug gives 0xFE)

    # Case 3: neg(-32768) = -32768 (wraps in 16-bit)
    e: int16 = -32768
    f: int16 = -e
    OCR0B.value  = uint8(f & 0xFF)         # low byte: 0x00
    OCR1AL.value = uint8((f >> 8) & 0xFF)  # high byte: 0x80

    asm("BREAK")
    while True:
        pass
