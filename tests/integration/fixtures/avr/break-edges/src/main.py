# PyMCU -- break-edges: edge cases verified via asm("BREAK") checkpoints
#
# No UART required: results are stored in ATmega328P general-purpose I/O registers
# (GPIOR0/1/2) and PORTB, whose data-space addresses are known at test time.
# The test suite uses RunToBreak() to halt at each checkpoint and inspect the
# simulator state directly.
#
# Data-space addresses (ATmega328P):
#   GPIOR0 = 0x3E   GPIOR1 = 0x4A   GPIOR2 = 0x4B   PORTB = 0x25
#
# Checkpoints:
#   1. GPIO high  + GPIOR0 = 0xAB
#   2. uint8 overflow: 200 + 100 = 300 -> wraps to 44 (0x2C)
#   3. while/break: i increments until it reaches 5, then exits the loop
#   4. @inline clamp with early return: clamp(200, 10, 100) = 100
#   5. uint16 byte extraction: 0x1234 -> lo=52 (0x34), hi=18 (0x12)
#
from pymcu.chips.atmega328p import PORTB, DDRB, GPIOR0, GPIOR1, GPIOR2
from pymcu.types import uint8, uint16, inline, asm


@inline
def clamp(x: uint8, lo: uint8, hi: uint8) -> uint8:
    if x < lo:
        return lo
    if x > hi:
        return hi
    return x


def main():
    DDRB[5] = 1      # PB5 as output (LED)

    # --- Checkpoint 1: GPIO high + GPIOR0 = 0xAB ---
    PORTB[5] = 1
    GPIOR0.value = 0xAB
    asm("BREAK")

    # --- Checkpoint 2: uint8 overflow (200 + 100 = 300 -> 0x2C = 44) ---
    a: uint8 = 200
    b: uint8 = 100
    c: uint8 = a + b   # 300 & 0xFF = 44
    PORTB[5] = 0
    GPIOR0.value = c
    asm("BREAK")

    # --- Checkpoint 3: while loop with break (i must be 5 at halt) ---
    i: uint8 = 0
    while True:
        if i == 5:
            break
        i += 1
    GPIOR0.value = i
    asm("BREAK")

    # --- Checkpoint 4: @inline clamp with early return (200 clamped to hi=100) ---
    result: uint8 = clamp(200, 10, 100)
    GPIOR0.value = result
    asm("BREAK")

    # --- Checkpoint 5: uint16 byte extraction into GPIOR1/GPIOR2 ---
    w: uint16 = 0x1234
    lo_byte: uint8 = w & 0xFF          # 0x34 = 52
    hi_byte: uint8 = (w >> 8) & 0xFF   # 0x12 = 18
    GPIOR1.value = lo_byte
    GPIOR2.value = hi_byte
    asm("BREAK")

    while True:
        pass
