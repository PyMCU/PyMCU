# PyMCU -- uint32-roundtrip: store a uint32 value and read back all 4 bytes.
#
# Verifies that uint32 variables are stored and loaded correctly (4 bytes).
# Before the fix, SizeOf(UINT32)=4 but is16=(4==2)=false, so only 1 byte
# was stored/loaded.
#
# Value: 0x12345678
#   byte0 (LSB) = 0x78, byte1 = 0x56, byte2 = 0x34, byte3 (MSB) = 0x12
#
# SRAM addresses used for output:
#   GPIOR0 = 0x3E -> byte0 = 0x78
#   GPIOR1 = 0x4A -> byte1 = 0x56
#   GPIOR2 = 0x4B -> byte2 = 0x34
#   OCR0A  = 0x47 -> byte3 = 0x12
#
from pymcu.types import uint8, uint32, asm
from pymcu.chips.atmega328p import GPIOR0, GPIOR1, GPIOR2, OCR0A


def b0(v: uint32) -> uint8:
    return v & 0xFF


def b1(v: uint32) -> uint8:
    return (v >> 8) & 0xFF


def b2(v: uint32) -> uint8:
    return (v >> 16) & 0xFF


def b3(v: uint32) -> uint8:
    return (v >> 24) & 0xFF


def main():
    val: uint32 = 0x12345678
    GPIOR0.value = b0(val)
    GPIOR1.value = b1(val)
    GPIOR2.value = b2(val)
    OCR0A.value  = b3(val)
    asm("BREAK")

    while True:
        pass

