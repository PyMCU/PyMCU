# PyMCU -- reversed-named-array: reversed() over a named CT array variable.
#
# Tests the code path where reversed() receives a named array variable
# (not an inline list literal) as its argument.  The compiler resolves
# the array from arraySizes and iterates the constant slots in reverse.
#
# vals = [5, 10, 15, 20]  reversed order: 20, 15, 10, 5  sum = 50 = 0x32
#
# Checkpoint 1: total = sum(reversed(vals)) = 50 = 0x32
#   GPIOR0 = 0x32, BREAK
#
# Checkpoint 2: reversed on a 3-element array [7, 14, 21]
#   reversed order: 21, 14, 7  sum = 42 = 0x2A
#   GPIOR0 = 0x2A, BREAK
#
# Data-space address (ATmega328P): GPIOR0 = 0x3E
#
from pymcu.types import uint8, asm
from pymcu.chips.atmega328p import GPIOR0


def main():
    # Checkpoint 1: reversed over 4-element named array
    vals: uint8[4] = [v for v in [5, 10, 15, 20]]
    total: uint8 = 0
    for v in reversed(vals):
        total = total + v
    GPIOR0.value = total
    asm("BREAK")

    # Checkpoint 2: reversed over 3-element named array
    trio: uint8[3] = [v for v in [7, 14, 21]]
    total2: uint8 = 0
    for v in reversed(trio):
        total2 = total2 + v
    GPIOR0.value = total2
    asm("BREAK")

    while True:
        pass
