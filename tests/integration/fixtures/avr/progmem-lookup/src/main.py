# PyMCU -- progmem-lookup: const[uint8[N]] flash (PROGMEM) lookup table.
#
# Tests: a 4-element byte lookup table placed in .text section and accessed
#        via LPM Z instruction.  Values must survive across checkpoints.
#
# Table: SIN_4 = [0, 64, 127, 64]  (quarter sine-wave approximation)
#
# Checkpoint 1: read SIN_4[0] = 0
#   GPIOR0 = 0x00
# Checkpoint 2: read SIN_4[1] = 64
#   GPIOR0 = 0x40
# Checkpoint 3: read SIN_4[2] = 127
#   GPIOR0 = 0x7F
# Checkpoint 4: read SIN_4[3] = 64
#   GPIOR0 = 0x40
#
# Data-space address (ATmega328P):
#   GPIOR0 = 0x3E
#
from pymcu.types import uint8, asm
from pymcu.chips.atmega328p import GPIOR0

SIN_4: const[uint8[4]] = [0, 64, 127, 64]


def main():
    # Checkpoint 1: SIN_4[0] = 0
    GPIOR0.value = SIN_4[0]
    asm("BREAK")

    # Checkpoint 2: SIN_4[1] = 64
    GPIOR0.value = SIN_4[1]
    asm("BREAK")

    # Checkpoint 3: SIN_4[2] = 127
    GPIOR0.value = SIN_4[2]
    asm("BREAK")

    # Checkpoint 4: SIN_4[3] = 64 (using variable index)
    idx: uint8 = 3
    GPIOR0.value = SIN_4[idx]
    asm("BREAK")

    while True:
        pass
