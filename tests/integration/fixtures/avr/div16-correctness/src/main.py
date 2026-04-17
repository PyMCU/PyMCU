# PyMCU -- div16-correctness: uint16 division and modulo must use __div16/__mod16.
#
# Bug: the old codegen emitted CALL __div8 regardless of type, truncating the
# 16-bit dividend to 8 bits and producing wrong results for values > 255.
#
# Tests:
#   1. 1000 / 10  = 100 (fits in uint16, dividend > 255)
#   2. 1000 % 10  = 0
#   3. 65000 / 256 = 253 (large dividend)
#   4. 65000 % 256 = 232
#
# Checkpoints (ATmega328P data-space):
#   GPIOR0 (0x3E) = low  byte of (1000 / 10) = 100
#   GPIOR1 (0x4A) = high byte of (1000 / 10) = 0
#   GPIOR2 (0x4B) = low  byte of (1000 % 10) = 0
#   OCR0A  (0x47) = low  byte of (65000 / 256) = 253
#   OCR0B  (0x48) = high byte of (65000 / 256) = 0
#   OCR1AL (0x88) = low  byte of (65000 % 256) = 232
#
from pymcu.types import uint8, uint16, asm
from pymcu.chips.atmega328p import GPIOR0, GPIOR1, GPIOR2, OCR0A, OCR0B, OCR1AL


def main():
    # Test 1 / Test 2: 1000 / 10, 1000 % 10
    a: uint16 = 1000
    b: uint16 = 10
    q1: uint16 = a / b
    r1: uint16 = a % b
    GPIOR0.value = uint8(q1 & 0xFF)          # 100
    GPIOR1.value = uint8((q1 >> 8) & 0xFF)   # 0
    GPIOR2.value = uint8(r1 & 0xFF)          # 0

    # Test 3 / Test 4: 65000 / 256, 65000 % 256 (large operands > 255)
    c: uint16 = 65000
    d: uint16 = 256
    q2: uint16 = c / d
    r2: uint16 = c % d
    OCR0A.value  = uint8(q2 & 0xFF)          # 253
    OCR0B.value  = uint8((q2 >> 8) & 0xFF)   # 0
    OCR1AL.value = uint8(r2 & 0xFF)          # 232

    asm("BREAK")
    while True:
        pass
