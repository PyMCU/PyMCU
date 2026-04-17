# PyMCU -- mul16-signed: signed int16 multiplication must use MULSU for cross-terms.
#
# Bug: the old codegen used unsigned MUL for all partial products. For int16 operands
# with negative values (high byte = 0xFF), this produced wrong sign-extended results.
#
# The fix replaces the two cross-term MUL instructions with MULSU (signed x unsigned)
# after copying a_hi and a_lo into R16-R23 range registers (R22, R23).
#
# Checkpoint 1: int16(-1) * int16(1) = -1 = 0xFFFF
#   GPIOR0 = low  byte = 0xFF
#   GPIOR1 = high byte = 0xFF
#
# Checkpoint 2: int16(-100) * int16(50) = -5000 = 0xEC78
#   GPIOR0 = low  byte = 0x78
#   GPIOR1 = high byte = 0xEC
#
# Checkpoint 3: int16(200) * int16(-3) = -600 = 0xFDA8
#   GPIOR0 = low  byte = 0xA8
#   GPIOR1 = high byte = 0xFD
#
# Data-space addresses (ATmega328P):
#   GPIOR0 = 0x3E   GPIOR1 = 0x4A
#
from pymcu.types import uint8, int16, asm
from pymcu.chips.atmega328p import GPIOR0, GPIOR1


def mul_i16(a: int16, b: int16) -> int16:
    return a * b


def lo(v: int16) -> uint8:
    return uint8(v & 0xFF)


def hi(v: int16) -> uint8:
    return uint8((v >> 8) & 0xFF)


def main():
    # --- Checkpoint 1: (-1) * 1 = -1 = 0xFFFF ---
    r1: int16 = mul_i16(-1, 1)
    GPIOR0.value = lo(r1)
    GPIOR1.value = hi(r1)
    asm("BREAK")

    # --- Checkpoint 2: (-100) * 50 = -5000 = 0xEC78 ---
    r2: int16 = mul_i16(-100, 50)
    GPIOR0.value = lo(r2)
    GPIOR1.value = hi(r2)
    asm("BREAK")

    # --- Checkpoint 3: 200 * (-3) = -600 = 0xFDA8 ---
    r3: int16 = mul_i16(200, -3)
    GPIOR0.value = lo(r3)
    GPIOR1.value = hi(r3)
    asm("BREAK")

    while True:
        pass
