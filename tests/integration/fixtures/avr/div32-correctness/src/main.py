# PyMCU -- div32-correctness: uint32 division and modulo must use __div32/__mod32.
#
# Bug: the old codegen emitted CALL __div8 regardless of type, truncating the
# 32-bit dividend to 8 bits and producing completely wrong results.
#
# Tests:
#   1. 100000 / 1000 = 100
#   2. 100000 % 1000 = 0
#   3. 1000000 / 300 = 3333
#   4. 1000000 % 300 = 100
#
# Checkpoints (ATmega328P data-space):
#   GPIOR0 (0x3E) = low  byte of (100000 / 1000) = 100
#   GPIOR1 (0x4A) = high byte of (100000 / 1000) = 0
#   GPIOR2 (0x4B) = low  byte of (100000 % 1000) = 0
#   OCR0A  (0x47) = low  byte of (1000000 / 300) = 0x05 (3333 & 0xFF)
#   OCR0B  (0x48) = high byte of (1000000 / 300) = 0x0D ((3333 >> 8) & 0xFF)
#   OCR1AL (0x88) = low  byte of (1000000 % 300) = 100
#
from pymcu.types import uint8, uint32, asm
from pymcu.chips.atmega328p import GPIOR0, GPIOR1, GPIOR2, OCR0A, OCR0B, OCR1AL


def main():
    # Test 1 / Test 2: 100000 / 1000, 100000 % 1000
    a: uint32 = 100000
    b: uint32 = 1000
    q1: uint32 = a / b
    r1: uint32 = a % b
    GPIOR0.value = uint8(q1 & 0xFF)          # 100
    GPIOR1.value = uint8((q1 >> 8) & 0xFF)   # 0
    GPIOR2.value = uint8(r1 & 0xFF)          # 0

    # Test 3 / Test 4: 1000000 / 300, 1000000 % 300
    c: uint32 = 1000000
    d: uint32 = 300
    q2: uint32 = c / d
    r2: uint32 = c % d
    OCR0A.value  = uint8(q2 & 0xFF)          # 3333 & 0xFF = 5
    OCR0B.value  = uint8((q2 >> 8) & 0xFF)   # (3333 >> 8) & 0xFF = 13
    OCR1AL.value = uint8(r2 & 0xFF)          # 100

    asm("BREAK")
    while True:
        pass
