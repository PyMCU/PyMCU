# PyMCU -- signed-rshift: int8 right-shift must use ASR (arithmetic) not LSR (logical).
#
# Bug: LSR shifts in a 0 from MSB (logical), giving wrong sign.
# Fix: ASR preserves MSB (arithmetic shift), propagating the sign bit.
#
# Checkpoint 1: -8 (0xF8) >> 1 must be -4 (0xFC) with ASR.
#   With LSR: 0xF8 >> 1 = 0x7C = 124 (wrong sign).
#   GPIOR0 = 0xFC (252 as uint8)
#
# Checkpoint 2: -128 (0x80) >> 7 must be -1 (0xFF) with ASR.
#   With LSR: 0x80 >> 7 = 0x01 (wrong).
#   GPIOR1 = 0xFF (255 as uint8)
#
# Checkpoint 3: -32 (0xE0) >> 3 must be -4 (0xFC) with ASR.
#   GPIOR2 = 0xFC
#
# Data-space addresses (ATmega328P):
#   GPIOR0 = 0x3E, GPIOR1 = 0x4A, GPIOR2 = 0x4B
#
from pymcu.types import int8, uint8, asm
from pymcu.chips.atmega328p import GPIOR0, GPIOR1, GPIOR2


def rshift1(v: int8) -> int8:
    return v >> 1


def rshift7(v: int8) -> int8:
    return v >> 7


def rshift3(v: int8) -> int8:
    return v >> 3


def main():
    a: int8 = -8
    GPIOR0.value = uint8(rshift1(a))

    b: int8 = -128
    GPIOR1.value = uint8(rshift7(b))

    c: int8 = -32
    GPIOR2.value = uint8(rshift3(c))

    asm("BREAK")

    while True:
        pass

