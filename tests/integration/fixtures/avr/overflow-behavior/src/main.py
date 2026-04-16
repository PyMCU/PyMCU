# PyMCU -- overflow-behavior: Integer overflow/underflow wrapping verification
#
# Tests that integer arithmetic wraps correctly for all signed/unsigned types
# when values exceed the representable range. This is critical for correctness
# and matches C/avr-gcc behavior.
#
# Checkpoints (data-space addresses on ATmega328P):
#   GPIOR0 (0x3E) = uint8 overflow test (255 + 1 = 0)
#   GPIOR1 (0x4A) = uint8 underflow test (0 - 1 = 255)
#   GPIOR2 (0x4B) = int8 overflow test (127 + 1 = -128, stored as 0x80)
#   OCR0A  (0x47) = int8 underflow test (-128 - 1 = 127, stored as 0x7F)
#   OCR0B  (0x48) = uint16 overflow high byte (65535 + 1 = 0)
#   OCR1AH (0x89) = int16 overflow result (-32768 - 1 = 32767, stored as 0x7FFF)
#
from pymcu.types import uint8, int8, uint16, int16, asm
from pymcu.chips.atmega328p import GPIOR0, GPIOR1, GPIOR2, OCR0A, OCR0B, OCR1AH

def main():
    # uint8: 255 + 1 = 0 (wrap around)
    a: uint8 = 255
    b: uint8 = a + 1
    GPIOR0.value = b  # Should be 0
    
    # uint8: 0 - 1 = 255 (underflow wrap)
    c: uint8 = 0
    d: uint8 = c - 1
    GPIOR1.value = d  # Should be 255 (0xFF)
    
    # int8: 127 + 1 = -128 (signed overflow)
    e: int8 = 127
    f: int8 = e + 1
    GPIOR2.value = uint8(f)  # Should be 0x80 (-128 as unsigned)
    
    # int8: -128 - 1 = 127 (signed underflow)
    g: int8 = -128
    h: int8 = g - 1
    OCR0A.value = uint8(h)  # Should be 0x7F (127 as unsigned)
    
    # uint16: 65535 + 1 = 0
    i: uint16 = 65535
    j: uint16 = i + 1
    OCR0B.value = uint8((j >> 8) & 0xFF)  # High byte should be 0
    
    # int16: -32768 - 1 = 32767
    k: int16 = -32768
    l: int16 = k - 1
    OCR1AH.value = uint8((l >> 8) & 0xFF)  # High byte should be 0x7F
    
    asm("BREAK")
    while True:
        pass

