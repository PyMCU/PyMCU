# PyMCU -- augmented-sreg: AugAssign operator SREG flag verification.
#
# AugAssign (|=, &=, ^=) uses a separate IR node and codegen path from plain
# Assign. This fixture verifies that OR/AND/EOR instructions are correctly
# emitted AND that SREG flags reflect their results.
#
# AVR flag behaviour for OR/AND/EOR:
#   N = result[7]  (negative: bit 7 set)
#   Z = (result == 0)
#   V = 0          (always cleared by logical instructions)
#   C unchanged    (not modified by OR/AND/EOR)
#
# Non-inline wrappers (or_u8, and_u8, xor_u8) receive operands as function
# parameters (R24, R22 from calling convention), making them runtime values
# from the callee's perspective. This prevents CT folding of the operations.
# After the ALU instruction: OUT/MOV do NOT touch SREG; SREG at BREAK reflects
# the last OR/AND/EOR executed inside the wrapper.
#
# Checkpoints:
#   1 -- or_u8(0x00, 0x80)  = 0x80; N=1 (bit7 set), Z=0
#   2 -- and_u8(0xF0, 0x0F) = 0x00; N=0, Z=1 (result is zero)
#   3 -- xor_u8(0xAA, 0xAA) = 0x00; N=0, Z=1 (self-cancellation)
#
# Data-space addresses:
#   GPIOR0 = 0x3E
from pymcu.chips.atmega328p import GPIOR0
from pymcu.types import uint8, asm


def or_u8(a: uint8, b: uint8) -> uint8:
    """Emits OR Ra, Rb (register-register) from the AugAssign IR node."""
    a |= b
    return a


def and_u8(a: uint8, b: uint8) -> uint8:
    """Emits AND Ra, Rb from the AugAssign IR node."""
    a &= b
    return a


def xor_u8(a: uint8, b: uint8) -> uint8:
    """Emits EOR Ra, Rb from the AugAssign IR node."""
    a ^= b
    return a


def main():
    # --- Checkpoint 1: OR -- 0x00 | 0x80 = 0x80 ---
    # OR Ra, Rb; Ra = 0x80; N=1 (bit7=1), Z=0, V=0
    r1: uint8 = or_u8(0x00, 0x80)
    GPIOR0.value = r1   # 0x80 = 128
    asm("BREAK")

    # --- Checkpoint 2: AND -- 0xF0 & 0x0F = 0x00 ---
    # AND Ra, Rb; Ra = 0x00; N=0, Z=1, V=0
    r2: uint8 = and_u8(0xF0, 0x0F)
    GPIOR0.value = r2   # 0x00 = 0
    asm("BREAK")

    # --- Checkpoint 3: XOR self-cancel -- 0xAA ^ 0xAA = 0x00 ---
    # EOR Ra, Ra; Ra = 0x00; N=0, Z=1, V=0
    r3: uint8 = xor_u8(0xAA, 0xAA)
    GPIOR0.value = r3   # 0x00 = 0
    asm("BREAK")

    while True:
        pass
