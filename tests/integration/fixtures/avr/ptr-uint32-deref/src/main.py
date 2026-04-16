# PyMCU -- ptr-uint32-deref: ptr[uint32] address storage is 16-bit, deref reads 4 bytes.
#
# Verifies two things:
#   1. A ptr[uint32] variable stores the ADDRESS (16-bit), not the pointed-to value.
#   2. Dereferencing a ptr[uint32] reads all 4 bytes correctly.
#
# Strategy: write known bytes to SRAM addresses 0x0200-0x0203 via four
# consecutive ptr[uint8] writes, then read them back via a ptr[uint32].
#
# Expected: reading 0x0200 as ptr[uint32] yields 0x04030201 (little-endian).
#
# SRAM addresses used for output:
#   GPIOR0 = 0x3E -> byte0 = 0x01
#   GPIOR1 = 0x4A -> byte1 = 0x02
#   GPIOR2 = 0x4B -> byte2 = 0x03
#   OCR0A  = 0x47 -> byte3 = 0x04
#
from pymcu.types import uint8, uint32, ptr, asm
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
    # Write 4 known bytes to SRAM 0x0200-0x0203
    p0: ptr[uint8] = ptr(0x0200)
    p1: ptr[uint8] = ptr(0x0201)
    p2: ptr[uint8] = ptr(0x0202)
    p3: ptr[uint8] = ptr(0x0203)
    p0.value = 0x01
    p1.value = 0x02
    p2.value = 0x03
    p3.value = 0x04

    # Read back 4 bytes as uint32 (little-endian: byte0=0x01, byte3=0x04)
    p32: ptr[uint32] = ptr(0x0200)
    v: uint32 = p32.value

    GPIOR0.value = b0(v)
    GPIOR1.value = b1(v)
    GPIOR2.value = b2(v)
    OCR0A.value  = b3(v)
    asm("BREAK")

    while True:
        pass

