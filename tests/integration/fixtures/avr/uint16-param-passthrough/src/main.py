# PyMCU -- uint16-param-passthrough: verify 16-bit function parameters are passed correctly.
#
# Bug: function prologue only copied the low byte (R24) of a uint16 argument
# to its destination register/stack slot. The high byte (R25) was ignored.
#
# Checkpoint 1: double_u16(300) = 600 = 0x0258
#   GPIOR0 = low byte = 0x58, GPIOR1 = high byte = 0x02
#
# Checkpoint 2: pass_u16(0x1234) = 0x1234 (identity)
#   GPIOR0 = 0x34, GPIOR1 = 0x12
#
# Data-space addresses (ATmega328P):
#   GPIOR0 = 0x3E   GPIOR1 = 0x4A
#
from pymcu.types import uint8, uint16, asm
from pymcu.chips.atmega328p import GPIOR0, GPIOR1


def double_u16(x: uint16) -> uint16:
    return x + x


def pass_u16(x: uint16) -> uint16:
    return x


def split_lo(v: uint16) -> uint8:
    return v & 0xFF


def split_hi(v: uint16) -> uint8:
    return (v >> 8) & 0xFF


def main():
    # --- Checkpoint 1: double_u16(300) = 600 = 0x0258 ---
    r1: uint16 = double_u16(300)
    GPIOR0.value = split_lo(r1)
    GPIOR1.value = split_hi(r1)
    asm("BREAK")

    # --- Checkpoint 2: pass_u16(0x1234) = 0x1234 ---
    r2: uint16 = pass_u16(0x1234)
    GPIOR0.value = split_lo(r2)
    GPIOR1.value = split_hi(r2)
    asm("BREAK")

    while True:
        pass

