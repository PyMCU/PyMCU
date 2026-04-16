# PyMCU -- signed-extend: int8 -> int16 widening must sign-extend, not zero-extend.
#
# Bug: zero-extension gives wrong high byte for negative values.
#   int16(-5) from int8 -5 (0xFB) with zero-ext: 0x00FB (not -5 in int16).
#   int16(-5) from int8 -5 (0xFB) with sign-ext: 0xFFFB = -5 in int16. Correct.
#
# Checkpoint 1: int16(a) where a: int8 = -5
#   GPIOR0 = low byte  = 0xFB
#   GPIOR1 = high byte = 0xFF (sign-extended); with zero-ext = 0x00 (bug)
#
# Checkpoint 2: int16(b) where b: int8 = -128 (0x80)
#   GPIOR2 = low byte  = 0x80
#   OCR0A  = high byte = 0xFF; with zero-ext = 0x00 (bug)
#
# Data-space addresses (ATmega328P):
#   GPIOR0 = 0x3E, GPIOR1 = 0x4A, GPIOR2 = 0x4B, OCR0A = 0x47
#
from pymcu.types import int8, int16, uint8, asm
from pymcu.chips.atmega328p import GPIOR0, GPIOR1, GPIOR2, OCR0A


def widen(v: int8) -> int16:
    return int16(v)


def main():
    a: int8 = -5
    w1: int16 = widen(a)
    GPIOR0.value = uint8(w1 & 0xFF)
    GPIOR1.value = uint8((w1 >> 8) & 0xFF)

    b: int8 = -128
    w2: int16 = widen(b)
    GPIOR2.value = uint8(w2 & 0xFF)
    OCR0A.value  = uint8((w2 >> 8) & 0xFF)

    asm("BREAK")

    while True:
        pass

