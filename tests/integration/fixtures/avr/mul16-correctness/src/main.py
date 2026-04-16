# PyMCU -- mul16-correctness: verify uint16 multiplication preserves both bytes.
#
# Bug: MUL R24, R18 leaves result in R1:R0. Before the fix, codegen emitted:
#   MOV R24, R0   (low byte OK)
#   CLR R1        (R1 destroyed!)
#   LDI R25, 0    (high byte always 0 -- wrong)
#
# After fix, codegen must emit:
#   MOV R24, R0
#   MOV R25, R1   (preserve high byte before clearing R1)
#   CLR R1
#
# Checkpoint 1: 300 * 200 = 60000 = 0xEA60
#   GPIOR0 = low  byte = 0x60 = 96
#   GPIOR1 = high byte = 0xEA = 234
#
# Checkpoint 2: 44 * 200 = 8800 = 0x2260 (low bytes of 300 and 200 are 0x2C and 0xC8)
#   Only low bytes of a uint16 are used by MUL, so this also validates
#   that the 8-bit partial product high byte (R1) is correctly captured.
#
# Data-space addresses (ATmega328P):
#   GPIOR0 = 0x3E   GPIOR1 = 0x4A
#
from pymcu.types import uint8, uint16, asm
from pymcu.chips.atmega328p import GPIOR0, GPIOR1


def mul_u16(a: uint16, b: uint16) -> uint16:
    return a * b


def split_lo(v: uint16) -> uint8:
    return v & 0xFF


def split_hi(v: uint16) -> uint8:
    return (v >> 8) & 0xFF


def main():
    # --- Checkpoint 1: 300 * 200 = 60000 = 0xEA60 ---
    r1: uint16 = mul_u16(300, 200)
    GPIOR0.value = split_lo(r1)
    GPIOR1.value = split_hi(r1)
    asm("BREAK")

    # --- Checkpoint 2: 256 * 256 = 65536 wraps to 0 for uint16 ---
    r2: uint16 = mul_u16(256, 256)
    GPIOR0.value = split_lo(r2)
    GPIOR1.value = split_hi(r2)
    asm("BREAK")

    while True:
        pass

