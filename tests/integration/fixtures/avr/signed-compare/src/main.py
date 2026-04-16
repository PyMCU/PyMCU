# PyMCU -- signed-compare: signed int8 comparisons must use BRLT/BRGE, not BRLO/BRSH.
#
# With unsigned branches BRLO/BRSH:
#   -5 < 1  -> 0xFB < 0x01 = False (wrong, bug)
#   -1 > -10 -> 0xFF > 0xF6 = True (accidentally correct)
#   -10 < -5 -> 0xF6 < 0xFB = True (accidentally correct)
#   1 > -1  -> 0x01 > 0xFF = False (wrong, bug)
#
# With signed branches BRLT/BRGE all four pass.
#
# SRAM addresses used for output:
#   GPIOR0 = 0x3E -> 1 if -5 < 1  else 0 (signed LT)
#   GPIOR1 = 0x4A -> 1 if -1 > -10 else 0 (signed GT)
#   GPIOR2 = 0x4B -> 1 if -10 < -5 else 0 (signed LT)
#   OCR0A  = 0x47 -> 1 if 1 > -1  else 0 (signed GT)
#
from pymcu.types import int8, asm
from pymcu.chips.atmega328p import GPIOR0, GPIOR1, GPIOR2, OCR0A


def main():
    a: int8 = -5
    b: int8 = 1
    if a < b:
        GPIOR0.value = 1
    else:
        GPIOR0.value = 0

    c: int8 = -1
    d: int8 = -10
    if c > d:
        GPIOR1.value = 1
    else:
        GPIOR1.value = 0

    e: int8 = -10
    f: int8 = -5
    if e < f:
        GPIOR2.value = 1
    else:
        GPIOR2.value = 0

    g: int8 = 1
    h: int8 = -1
    if g > h:
        OCR0A.value = 1
    else:
        OCR0A.value = 0

    asm("BREAK")

    while True:
        pass

