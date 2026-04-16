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
# Checkpoint 2: 256 * 256 = 65536, wraps to 0 for uint16
#   GPIOR0 = 0x00
#   GPIOR1 = 0x00
#
# Data-space addresses (ATmega328P):
#   GPIOR0 = 0x3E   GPIOR1 = 0x4A
#
from pymcu.types import uint8, uint16, asm
from pymcu.chips.atmega328p import GPIOR0, GPIOR1


def mul_u16(a: uint16, b: uint16) -> uint16:
    return a * b


def main():
    # --- Checkpoint 1: 300 * 200 = 60000 = 0xEA60 ---
    r1: uint16 = mul_u16(300, 200)
    GPIOR0.value = uint8(r1 & 0xFF)
    GPIOR1.value = uint8((r1 >> 8) & 0xFF)
    asm("BREAK")

    # --- Checkpoint 2: 256 * 256 = 65536 wraps to 0 ---
    r2: uint16 = mul_u16(256, 256)
    GPIOR0.value = uint8(r2 & 0xFF)
    GPIOR1.value = uint8((r2 >> 8) & 0xFF)
    asm("BREAK")

    while True:
        pass

